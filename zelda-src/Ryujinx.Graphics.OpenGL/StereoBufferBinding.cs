using System;
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
