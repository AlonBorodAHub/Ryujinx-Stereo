using System;
using OpenTK.Graphics.OpenGL;
using System.Collections.Generic;
using System.Threading;
using Ryujinx.Graphics.GAL;
namespace Ryujinx.Graphics.OpenGL
{
    internal static class StereoBufferBinding
    {
        [ThreadStatic] private static Dictionary<(bool Storage, int Binding), BufferRange> _ranges;
        [ThreadStatic] private static long _seenDeletion;
        private static long _deletions;
        [ThreadStatic] private static int _uniformHandle, _storageHandle;
        [ThreadStatic] private static bool _uniformKnown, _storageKnown;
        [ThreadStatic] private static long _genericDeletion;
        internal static long GenericBindSkips;
        private static void CheckGenericDeletion()
        {
            long revision = DeletionGeneration;
            if (revision == _genericDeletion) return;
            _uniformKnown = _storageKnown = false;
            _genericDeletion = revision;
        }
        internal static void BindGeneric(BufferTarget target, int handle)
        {
            CheckGenericDeletion();
            bool storage = target == BufferTarget.ShaderStorageBuffer;
            if (NativeStereo.Enabled && NativeStereo.CacheBufferBindings &&
                (storage ? _storageKnown && _storageHandle == handle : _uniformKnown && _uniformHandle == handle))
            { GenericBindSkips++; return; }
            GL.BindBuffer(target, handle);
            RecordGeneric(storage, handle);
        }
        private static void RecordGeneric(bool storage, int handle)
        {
            if (storage) { _storageHandle = handle; _storageKnown = true; }
            else { _uniformHandle = handle; _uniformKnown = true; }
        }
        internal static void BindRange(BufferRangeTarget target, int binding, int handle, nint offset, nint size)
        {
            CheckGenericDeletion();
            GL.BindBufferRange(target, binding, handle, offset, size);
            // Unlike multi-bind, a single indexed bind also changes the generic target.
            RecordGeneric(target == BufferRangeTarget.ShaderStorageBuffer, handle);
        }
        internal static long DeletionGeneration => Volatile.Read(ref _deletions);
        internal static void Deleted() => Interlocked.Increment(ref _deletions);
        internal static void Invalidate() => _ranges?.Clear();
        internal static int Filter(ReadOnlySpan<BufferAssignment> source, Span<BufferAssignment> changed, bool storage)
        {
            _ranges ??= new();
            long revision = Volatile.Read(ref _deletions);
            if (revision != _seenDeletion) { _ranges.Clear(); _seenDeletion = revision; }
            int count = 0;
            foreach (var assignment in source)
            {
                var range = assignment.Range;
                var key = (storage, assignment.Binding);
                if (_ranges.TryGetValue(key, out var old) && old.Handle == range.Handle &&
                    (range.Handle == BufferHandle.Null || (old.Offset == range.Offset && old.Size == range.Size))) continue;
                _ranges[key] = range;
                changed[count++] = assignment;
            }
            return count;
        }
    }
}
