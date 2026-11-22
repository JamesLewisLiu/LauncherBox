using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace LauncherBox {
    internal sealed class Sqlite : IDisposable {
        private static readonly IntPtr Transient = new IntPtr(-1);
        private IntPtr handle;

        internal Sqlite(string path) {
            try {
                // SQLITE_OPEN_READWRITE | SQLITE_OPEN_CREATE
                Check(sqlite3_open_v2(Utf8(path), out handle, 6, IntPtr.Zero));
                Check(sqlite3_busy_timeout(handle, 5000));
            } catch {
                Dispose();
                throw;
            }
        }

        internal Statement Prepare(string sql) {
            IntPtr statement;
            Check(sqlite3_prepare_v2(handle, Utf8(sql), -1, out statement, IntPtr.Zero));
            return new Statement(this, statement);
        }

        internal void Execute(string sql, params object[] values) {
            using (Statement statement = Prepare(sql)) statement.Run(values);
        }

        internal T Write<T>(Func<T> operation) {
            Execute("BEGIN IMMEDIATE");
            try {
                T result = operation();
                Execute("COMMIT");
                return result;
            } catch {
                if (sqlite3_get_autocommit(handle) == 0) Execute("ROLLBACK");
                throw;
            }
        }

        private void Check(int result) {
            if (result == 0) return;
            IntPtr message = handle == IntPtr.Zero ? IntPtr.Zero : sqlite3_errmsg(handle);
            int length = 0;
            if (message != IntPtr.Zero) {
                while (Marshal.ReadByte(message, length) != 0) length++;
            }
            throw new IOException("Could not access LauncherBox.db (SQLite " + result + "). " +
                (message == IntPtr.Zero ? "" : Encoding.UTF8.GetString(Copy(message, length))));
        }

        private static byte[] Utf8(string value) {
            return Encoding.UTF8.GetBytes(value + "\0");
        }

        private static byte[] Copy(IntPtr pointer, int length) {
            byte[] bytes = new byte[length];
            if (length > 0) Marshal.Copy(pointer, bytes, 0, length);
            return bytes;
        }

        public void Dispose() {
            if (handle == IntPtr.Zero) return;
            sqlite3_close_v2(handle);
            handle = IntPtr.Zero;
        }

        internal sealed class Statement : IDisposable {
            private readonly Sqlite database;
            private readonly IntPtr handle;

            internal Statement(Sqlite database, IntPtr handle) {
                this.database = database;
                this.handle = handle;
            }

            internal void Bind(params object[] values) {
                for (int i = 0; i < values.Length; i++) {
                    object value = values[i];
                    byte[] blob = value as byte[];
                    int index = i + 1;
                    int result;
                    if (value == null) result = sqlite3_bind_null(handle, index);
                    else if (value is int) result = sqlite3_bind_int(handle, index, (int)value);
                    else if (blob != null) result = sqlite3_bind_blob(handle, index, blob, blob.Length, Transient);
                    else {
                        byte[] text = Utf8((string)value);
                        result = sqlite3_bind_text(handle, index, text, text.Length - 1, Transient);
                    }
                    database.Check(result);
                }
            }

            internal bool Next() {
                int result = sqlite3_step(handle);
                if (result == 100) return true;  // SQLITE_ROW
                if (result == 101) return false; // SQLITE_DONE
                database.Check(result);
                throw new InvalidDataException("SQLite did not return a row or finish the statement.");
            }

            internal void Run(params object[] values) {
                Bind(values);
                while (Next()) { }
                database.Check(sqlite3_reset(handle));
            }

            internal int Int(int column) {
                return sqlite3_column_int(handle, column);
            }

            internal string Text(int column) {
                IntPtr text = sqlite3_column_text(handle, column);
                return text == IntPtr.Zero ? null : Encoding.UTF8.GetString(Copy(text, sqlite3_column_bytes(handle, column)));
            }

            internal byte[] Blob(int column) {
                IntPtr data = sqlite3_column_blob(handle, column);
                return data == IntPtr.Zero ? null : Copy(data, sqlite3_column_bytes(handle, column));
            }

            public void Dispose() {
                sqlite3_finalize(handle);
            }
        }

        // Use the Windows SQLite library even when another DLL is next to the elevated executable.
        private const string Library = "winsqlite3.dll";
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int sqlite3_open_v2(byte[] path, out IntPtr database, int flags, IntPtr vfs);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int sqlite3_busy_timeout(IntPtr database, int milliseconds);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int sqlite3_get_autocommit(IntPtr database);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern IntPtr sqlite3_errmsg(IntPtr database);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int sqlite3_close_v2(IntPtr database);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int sqlite3_prepare_v2(IntPtr database, byte[] sql, int length, out IntPtr statement, IntPtr tail);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int sqlite3_finalize(IntPtr statement);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int sqlite3_step(IntPtr statement);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int sqlite3_reset(IntPtr statement);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int sqlite3_bind_null(IntPtr statement, int index);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int sqlite3_bind_int(IntPtr statement, int index, int value);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int sqlite3_bind_text(IntPtr statement, int index, byte[] text, int length, IntPtr destructor);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int sqlite3_bind_blob(IntPtr statement, int index, byte[] data, int length, IntPtr destructor);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int sqlite3_column_int(IntPtr statement, int index);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern IntPtr sqlite3_column_text(IntPtr statement, int index);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern IntPtr sqlite3_column_blob(IntPtr statement, int index);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int sqlite3_column_bytes(IntPtr statement, int index);
    }
}
