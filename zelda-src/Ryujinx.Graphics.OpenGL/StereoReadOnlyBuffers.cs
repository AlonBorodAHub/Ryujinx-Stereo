using System.Text.RegularExpressions;

namespace Ryujinx.Graphics.OpenGL;

// Conservative recognizer for the translator's scalar storage-buffer loads.
// Unknown syntax, writes, atomics and images retain the two-draw path.
internal static class StereoReadOnlyBuffers
{
    internal static bool CanBatch(string source)
    {
        source = Regex.Replace(source, @"/\*[\s\S]*?\*/|//[^\r\n]*", "");
        if (Regex.IsMatch(source, @"\bimage\w*\b|\batomic\w*\b|^\s*#\s*define\b", RegexOptions.Multiline)) return false;
        var blocks = Regex.Matches(source, @"\bbuffer\s+\w+\s*\{\s*uint\s+data\s*\[\s*\]\s*;\s*\}\s*(\w+)\s*;");
        string body = Regex.Replace(source, @"\bbuffer\s+\w+\s*\{\s*uint\s+data\s*\[\s*\]\s*;\s*\}\s*\w+\s*;", "");
        if (Regex.IsMatch(body, @"\bbuffer\b")) return false;
        foreach (Match block in blocks)
        {
            string name = Regex.Escape(block.Groups[1].Value);
            // Only a literal or a scalar temporary index, with an optional bitcast.
            // This deliberately rejects more complex reads rather than guessing.
            string load = @"^\s*temp_\d+\s*=\s*(?:uintBitsToFloat\(\s*)?" + name + @"\.data\[(?:\d+|temp_\d+)\]\s*\)?\s*;\s*$";
            foreach (string line in body.Split('\n'))
                if (Regex.IsMatch(line, @"\b" + name + @"\b") && !Regex.IsMatch(line, load)) return false;
        }
        return true;
    }
}
