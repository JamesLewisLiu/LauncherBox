using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using System.Xml.Serialization;

namespace LauncherBox {
    internal static partial class Store {
        private const int SchemaVersion = 1;

        private static Sqlite OpenSettings(bool protectedStartup = false) {
            Sqlite db = new Sqlite(DatabasePath);
            try {
                int version = ReadSchemaVersion(db);
                if (version == 0) {
                    version = db.Write(delegate {
                        int current = ReadSchemaVersion(db);
                        if (current != 0) return current;
                        db.Execute("CREATE TABLE settings (id INTEGER PRIMARY KEY CHECK (id = 1), bindings_saved INTEGER NOT NULL)");
                        db.Execute("INSERT INTO settings VALUES (1, 0)");
                        db.Execute("CREATE TABLE games (position INTEGER PRIMARY KEY, name TEXT, bat TEXT, hash TEXT, art TEXT)");
                        db.Execute("CREATE TABLE screenshots (id TEXT PRIMARY KEY, png BLOB NOT NULL)");
                        db.Execute("CREATE TABLE bindings (position INTEGER PRIMARY KEY, action INTEGER NOT NULL, " +
                            "source INTEGER NOT NULL, device TEXT, code TEXT, label TEXT)");
                        ImportLegacy(db, protectedStartup);
                        db.Execute("PRAGMA user_version = " + SchemaVersion);
                        return SchemaVersion;
                    });
                }
                if (version != SchemaVersion) throw new InvalidDataException("LauncherBox.db uses an unsupported settings version.");
                return db;
            } catch {
                db.Dispose();
                throw;
            }
        }

        private static int ReadSchemaVersion(Sqlite db) {
            using (Sqlite.Statement row = db.Prepare("PRAGMA user_version")) {
                if (!row.Next()) throw new InvalidDataException("Could not read the LauncherBox.db settings version.");
                return row.Int(0);
            }
        }

        internal static List<Game> Load(bool protectedStartup) {
            using (Sqlite db = OpenSettings(protectedStartup)) return ReadGames(db);
        }

        private static List<Game> ReadGames(Sqlite db) {
            List<Game> games = new List<Game>();
            using (Sqlite.Statement row = db.Prepare("SELECT name, bat, hash, art FROM games ORDER BY position")) {
                while (row.Next()) {
                    games.Add(new Game { Name = row.Text(0), Bat = row.Text(1), Hash = row.Text(2), Art = row.Text(3) });
                }
            }
            return games;
        }

        internal static void Save(List<Game> original, List<Game> games, string art = null, byte[] png = null) {
            using (Sqlite db = OpenSettings()) {
                db.Write(delegate {
                    List<Game> current = ReadGames(db);
                    if (!SameGames(current, original))
                        throw new IOException("The game library changed in another LauncherBox session. Reopen LauncherBox and try again.");
                    if (png != null) SaveScreenshot(db, art, png);
                    WriteGames(db, games);
                    db.Execute("DELETE FROM screenshots WHERE NOT EXISTS (SELECT 1 FROM games WHERE games.art = screenshots.id)");
                    return true;
                });
            }
        }

        private static bool SameGames(List<Game> current, List<Game> original) {
            if (current.Count != original.Count) return false;
            for (int i = 0; i < current.Count; i++) {
                Game first = current[i];
                Game second = original[i];
                if (first.Name != second.Name || first.Bat != second.Bat || first.Hash != second.Hash || first.Art != second.Art)
                    return false;
            }
            return true;
        }

        private static void WriteGames(Sqlite db, List<Game> games) {
            db.Execute("DELETE FROM games");
            using (Sqlite.Statement insert = db.Prepare("INSERT INTO games (position, name, bat, hash, art) VALUES (?, ?, ?, ?, ?)")) {
                for (int i = 0; i < games.Count; i++) {
                    Game game = games[i];
                    insert.Run(i, game.Name, game.Bat, game.Hash, game.Art);
                }
            }
        }

        internal static List<InputBinding> LoadBindings() {
            using (Sqlite db = OpenSettings()) return ReadBindings(db);
        }

        private static List<InputBinding> ReadBindings(Sqlite db) {
            if (!HasSavedBindings(db)) return null;
            List<InputBinding> bindings = new List<InputBinding>();
            using (Sqlite.Statement row = db.Prepare("SELECT action, source, device, code, label FROM bindings ORDER BY position")) {
                while (row.Next()) {
                    bindings.Add(new InputBinding {
                        Action = (LauncherAction)row.Int(0), Source = (InputSource)row.Int(1), Device = row.Text(2),
                        Code = row.Text(3), Label = row.Text(4)
                    });
                }
            }
            return bindings;
        }

        private static bool HasSavedBindings(Sqlite db) {
            using (Sqlite.Statement row = db.Prepare("SELECT bindings_saved FROM settings WHERE id = 1")) {
                if (!row.Next()) throw new InvalidDataException("The LauncherBox.db settings row is missing.");
                return row.Int(0) != 0;
            }
        }

        internal static List<InputBinding> UpdateBindings(Func<List<InputBinding>, List<InputBinding>> update) {
            using (Sqlite db = OpenSettings()) {
                return db.Write(delegate {
                    List<InputBinding> updated = update(ReadBindings(db));
                    WriteBindings(db, updated);
                    return updated;
                });
            }
        }

        private static void WriteBindings(Sqlite db, List<InputBinding> bindings) {
            db.Execute("DELETE FROM bindings");
            using (Sqlite.Statement insert = db.Prepare(
                "INSERT INTO bindings (position, action, source, device, code, label) VALUES (?, ?, ?, ?, ?, ?)")) {
                for (int i = 0; i < bindings.Count; i++) {
                    InputBinding binding = bindings[i];
                    insert.Run(i, (int)binding.Action, (int)binding.Source, binding.Device, binding.Code, binding.Label);
                }
            }
            db.Execute("UPDATE settings SET bindings_saved = 1 WHERE id = 1");
        }

        private static void SaveScreenshot(Sqlite db, string key, byte[] png) {
            db.Execute("INSERT INTO screenshots (id, png) VALUES (?, ?)", key, png);
        }

        private static byte[] ReadScreenshot(string key) {
            if (String.IsNullOrEmpty(key)) return null;
            using (Sqlite db = OpenSettings())
            using (Sqlite.Statement row = db.Prepare("SELECT png FROM screenshots WHERE id = ?")) {
                row.Bind(key);
                return row.Next() ? row.Blob(0) : null;
            }
        }

        private static void ImportLegacy(Sqlite db, bool protectedStartup) {
            if (!Directory.Exists(LegacyRoot)) return;
            if (protectedStartup) Startup.RequireProtectedEntry(new DirectoryInfo(LegacyRoot));
            string profile = FindLegacyProfile();
            if (profile == null) return;
            if (protectedStartup) Startup.RequireProtectedEntry(new DirectoryInfo(profile));

            List<Game> games = ReadLegacy<List<Game>>(Path.Combine(profile, "games.xml"), protectedStartup);
            if (games != null) {
                games.RemoveAll(game => String.IsNullOrEmpty(game.Bat) && String.IsNullOrEmpty(game.Hash) &&
                    ((game.Name == "Moon Bay Rally" && game.Art == "rally.png") ||
                     (game.Name == "Starport Breakout" && game.Art == "orbit.png")));
                HashSet<string> imported = new HashSet<string>(StringComparer.Ordinal);
                foreach (Game game in games) {
                    string file = LegacyScreenshot(profile, game.Art);
                    if (file == null) {
                        game.Art = null;
                    } else if (imported.Add(game.Art)) {
                        if (protectedStartup) Startup.RequireProtectedEntry(new FileInfo(file));
                        SaveScreenshot(db, game.Art, File.ReadAllBytes(file));
                    }
                }
                WriteGames(db, games);
            }
            List<InputBinding> bindings = ReadLegacy<List<InputBinding>>(Path.Combine(profile, "bindings.xml"), protectedStartup);
            if (bindings != null) WriteBindings(db, bindings);
        }

        private static string LegacyScreenshot(string profile, string key) {
            Guid id;
            if (String.IsNullOrEmpty(key) || !Guid.TryParseExact(Path.GetFileNameWithoutExtension(key), "N", out id) ||
                key != id.ToString("N") + ".png") return null;
            string file = Path.Combine(profile, key);
            return File.Exists(file) ? file : null;
        }

        private static string FindLegacyProfile() {
            string current = Path.Combine(LegacyRoot, Sid);
            if (HasLegacySettings(current)) return current;
            string match = null;
            foreach (string profile in Directory.EnumerateDirectories(LegacyRoot, "S-1-*")) {
                if (!HasLegacySettings(profile)) continue;
                if (match != null)
                    throw new IOException("Multiple legacy profiles were found. Keep the profile you want to import in Profiles and move the others to a backup folder.");
                match = profile;
            }
            return match;
        }

        private static bool HasLegacySettings(string profile) {
            return File.Exists(Path.Combine(profile, "games.xml")) || File.Exists(Path.Combine(profile, "bindings.xml"));
        }

        private static T ReadLegacy<T>(string path, bool protectedStartup) where T : class {
            if (!File.Exists(path)) return null;
            if (protectedStartup) Startup.RequireProtectedEntry(new FileInfo(path));
            using (XmlReader xml = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null })) {
                return (T)new XmlSerializer(typeof(T)).Deserialize(xml);
            }
        }
    }
}
