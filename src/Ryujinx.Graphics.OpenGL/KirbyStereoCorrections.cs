using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Ryujinx.Common.Logging;
namespace Ryujinx.Graphics.OpenGL;
internal static class KirbyStereoCorrections
{
    internal static bool Enabled => string.Equals(Environment.GetEnvironmentVariable("RJ_STEREO_GAME"), "01004d300c5ae000", StringComparison.OrdinalIgnoreCase);
    internal static bool CameraMesh(string source)
    {
        if (!Enabled) return false;
        // HAL also draws camera-space mesh masks with depth testing disabled.
        // These use the same complete VP transform as the visible mesh.
        // Orthographic light cameras retain w=1 and are excluded by the shift guard.
        foreach (Match m in Regex.Matches(source, @"(vp_c\d+)\.data\[3\]\.w"))
        {
            string buffer = m.Groups[1].Value;
            if (source.Contains(buffer + ".data[0].x") && source.Contains(buffer + ".data[1].y") && source.Contains(buffer + ".data[2].z")) return true;
        }
        return false;
    }
    internal static string ProjectedVaryings(string source)
    {
        if (!Enabled) return "";
        string Last(string target) { var ms=Regex.Matches(source, $@"\b{Regex.Escape(target)} = (temp_\d+);"); return ms.Count==0 ? null : ms[^1].Groups[1].Value; }
        string x=Last("gl_Position.x"), y=Last("gl_Position.y"), w=Last("gl_Position.w");
        if(x==null||y==null||w==null)return "";
        var outputs=new List<string>();
        foreach(Match m in Regex.Matches(source,@"\b(out_attr\d+)\.x = (temp_\d+);"))
            if(m.Groups[2].Value==x && Last(m.Groups[1].Value+".y")==y && Last(m.Groups[1].Value+".w")==w && !outputs.Contains(m.Groups[1].Value)) outputs.Add(m.Groups[1].Value);
        return outputs.Count==0 ? "" : "if (abs(gl_Position.w - 1.0) > 0.0001) {\n"+string.Join("\n",outputs.Select(o=>$"{o}.x += ryu_stereo_projected_varying.x * (gl_Position.w - ryu_stereo_projected_varying.y);"))+"\n}\n";
    }
    internal static string Rewrite(string source)
    {
        if(!Enabled||source.Contains("ryu_kirby_receiver",StringComparison.Ordinal))return source;
        foreach(var layout in new[]{("fp_c5",10),("fp_c4",10),("fp_c3",0)})
        {
            string buffer=layout.Item1;int first=layout.Item2,last=first+3;
            var terms=new Dictionary<int,string>();string ndcX=null;
            foreach(Match m in Regex.Matches(source,$@"\b(temp_\d+) = (temp_\d+) \* {buffer}\.data\[(\d+)\]\.x;")){
                int row=int.Parse(m.Groups[3].Value);if(row<first||row>last)continue;
                if(ndcX!=null&&ndcX!=m.Groups[2].Value){terms.Clear();break;}ndcX=m.Groups[2].Value;terms[row]=m.Groups[1].Value;
            }
            if(terms.Count!=4)continue;
            var partials=new Dictionary<int,string>();string ndcY=null;
            foreach(Match m in Regex.Matches(source,$@"\b(temp_\d+) = fma\((temp_\d+), {buffer}\.data\[(\d+)\]\.y, (temp_\d+)\);")){
                int row=int.Parse(m.Groups[3].Value);if(!terms.TryGetValue(row,out string t)||t!=m.Groups[4].Value)continue;
                if(ndcY!=null&&ndcY!=m.Groups[2].Value){partials.Clear();break;}ndcY=m.Groups[2].Value;partials[row]=m.Groups[1].Value;
            }
            if(partials.Count!=4)continue;
            foreach(Match m in Regex.Matches(source,$@"\btemp_\d+ = fma\((temp_\d+), {buffer}\.data\[{last}\]\.z, {partials[last]}\);")){
                string z=m.Groups[1].Value;
                if(!Regex.IsMatch(source,$@"\b{z} = temp_\d+ \* 2\.0;"))continue;
                var zterms=new List<Match>();
                foreach(var row in partials){Match zm=Regex.Match(source,$@"\btemp_\d+ = fma\({z}, {buffer}\.data\[{row.Key}\]\.z, {row.Value}\);");if(zm.Success)zterms.Add(zm);}
                if(zterms.Count!=4)continue;int insert=zterms.Min(v=>v.Index);
                if(partials.Values.Any(v=>source.IndexOf(v+" =",StringComparison.Ordinal)>insert))continue;
                string correction=$"float ryu_kirby_receiver = ryu_stereo_deferred.x * (1.0 - ryu_stereo_deferred.y * (fma({z}, {buffer}.data[{last}].z, {partials[last]}) + {buffer}.data[{last}].w)) / (1.0 - ryu_stereo_deferred.x * ryu_stereo_deferred.y * {buffer}.data[{last}].x);\n";
                foreach(var row in partials)correction+=$"{row.Value} -= ryu_kirby_receiver * {buffer}.data[{row.Key}].x;\n";
                source=source.Insert(insert,correction);return Declare(source,"homogeneous shadow/decal receiver");
            }
        }
        // HAL's main deferred lighting reconstructs a near-plane ray and scales
        // it by signed view Z from the G-buffer, then transforms view to world.
        if(source.Contains("fp_c3.data[13].x",StringComparison.Ordinal)&&source.Contains("fp_c3.data[16].z",StringComparison.Ordinal)){
            var defs=new Dictionary<string,Match>();foreach(Match m in Regex.Matches(source,@"\b(temp_\d+) = ([^;]+);"))defs[m.Groups[1].Value]=m;
            Match world=Regex.Match(source,@"\btemp_\d+ = (temp_\d+) \* fp_c3\.data\[14\]\.x;");
            if(world.Success && defs.TryGetValue(world.Groups[1].Value,out Match pos)){
                Match product=Regex.Match(pos.Groups[2].Value,@"^(temp_\d+) \* (temp_\d+)$");
                if(product.Success && defs.TryGetValue(product.Groups[2].Value,out Match scale)){
                    Match scaled=Regex.Match(scale.Groups[2].Value,@"^(temp_\d+) \* (temp_\d+)$");
                    if(scaled.Success && defs.TryGetValue(scaled.Groups[2].Value,out Match inv) && Regex.IsMatch(inv.Groups[2].Value,@"^1\.0 / temp_\d+$")){
                        string viewZ=scaled.Groups[1].Value;
                        string nearZ = Regex.Match(inv.Groups[2].Value, @"temp_\d+$").Value;
                        if (!defs.TryGetValue(nearZ, out Match nearPoint)) return source;
                        Match homogeneousZ = Regex.Match(nearPoint.Groups[2].Value, @"^(temp_\d+) \* temp_\d+$");
                        if (!homogeneousZ.Success) return source;
                        string hz = homogeneousZ.Groups[1].Value;
                        source=source.Insert(pos.Index+pos.Length,$"\nfloat ryu_kirby_receiver = -ryu_stereo_deferred.x * ({viewZ} / {hz} - ryu_stereo_deferred.y) * fp_c3.data[10].x;\n{pos.Groups[1].Value} += ryu_kirby_receiver;\n");return Declare(source,"linear-depth lighting receiver");
                    }
                }
            }
        }
        return source;
    }
    private static string Declare(string source,string kind){int main=source.IndexOf("void main()",StringComparison.Ordinal);Logger.Notice.Print(LogClass.Gpu,"GeometryStereo: Kirby "+kind+" corrected");return source.Insert(main,"uniform vec2 ryu_stereo_deferred;\n");}
}
