﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>
    /// Represent a file in memory. Can be used for datafile, logfile or tempfile. Will have a single instance per engine instance/file
    /// Has an own instance of memory store (large byte[] in memory)
    /// [ThreadSafe]
    /// </summary>
    internal class MemoryFile : IDisposable
    {
        private readonly MemoryStore _store = new MemoryStore();

        private readonly ConcurrentBag<Stream> _pool = new ConcurrentBag<Stream>();

        private readonly IDiskFactory _factory;
        private readonly AesEncryption _aes;
        private readonly DbFileMode _mode;

        private readonly Lazy<MemoryFileWriter> _writer;

        public MemoryFile(IDiskFactory factory, AesEncryption aes, DbFileMode mode)
        {
            _factory = factory;
            _aes = aes;
            _mode = mode;

            _store = new MemoryStore();

            // create lazy writer to avoid create file if not needed (readonly file access)
            _writer = new Lazy<MemoryFileWriter>(() => new MemoryFileWriter(_factory.GetStream(true, _mode == DbFileMode.Walfile), _store, _aes, _mode));
        }

        /// <summary>
        /// Get file length (return 0 if not exists or if empty)
        /// </summary>
        public long Length => _factory.Exists() ? _writer.Value.Length : 0;

        /// <summary>
        /// Get how many bytes this file allocate inside heap memory
        /// </summary>
        public int MemoryBufferSize => _store.ExtendSegments * MEMORY_SEGMENT_SIZE * PAGE_SIZE;

        /// <summary>
        /// Get reader from pool (or from new instance). Must be Dispose after use to return to pool
        /// Should be one reader per thread (do not share same reader across many threads)
        /// </summary>
        public MemoryFileReader GetReader(bool writable)
        {
            // checks if pool contains already opened stream
            if (!_pool.TryTake(out var stream))
            {
                stream = _factory.GetStream(false, false);
            }

            // when reader dispose, return back stream to pool
            void disposing(Stream s) { _pool.Add(s); }

            // create new instance
            return new MemoryFileReader(_store, stream, _aes, writable, disposing);
        }

        /// <summary>
        /// Write page on file in async task (another thread)
        /// If file is AppendOnly (log file) page will be saved on end of file and will update Position. 
        /// Pending pages will be avaiable in reader
        /// To write any page you must get this page from Reader and dispose reader only after ready write all of them
        /// </summary>
        public void WriteAsync(IEnumerable<PageBuffer> pages)
        {
            using (var reader = pages.GetEnumerator())
            {
                while(reader.MoveNext())
                {
                    var page = reader.Current;

                    _writer.Value.QueuePage(page);
                }
            }

            _writer.Value.RunQueue();
        }

        /// <summary>
        /// Define new length to file stream in async queue
        /// </summary>
        public void SetLengthAsync(long length)
        {
            _writer.Value.QueueLength(length);

            _writer.Value.RunQueue();
        }

        public void Dispose()
        {
            // dispose all reader stream
            foreach(var stream in _pool)
            {
                stream.Dispose();
            }

            if (_writer.IsValueCreated)
            {
                // do writer dispose (wait async writer thread)
                _writer.Value.Dispose();
            }

            // stop cache async clean task
            _store.Dispose();
        }
    }
}