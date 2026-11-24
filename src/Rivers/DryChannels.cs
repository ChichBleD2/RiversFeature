using System;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using OpenTK.Mathematics;

namespace RiverFeatures.Rivers
{
    /// <summary>
    /// Dry channels (flight test 7): dry rivers (wadis) and dry oxbows. They are ordinary Rivers nodes, so Rivers lays out,
    /// carves and gravels them like any channel, but their carving depth is capped: Rivers carves a channel column from
    /// (sea + heightBoost - depth) up (read from IL, RiversApi.ShouldBeCarved / NewGenTerra), with depth = bankFactor x
    /// (map height - sea) and water only below sea level. Capping bankFactor at (heightBoost - DryFloorAboveSea) / (map height -
    /// sea) keeps the channel floor DryFloorAboveSea blocks above the sea: a dry, gravelled channel, never water. A dry river's
    /// mouth node also fades its depth to 0 over its downstream 60%, so the wadi runs out into nowhere instead of reaching the
    /// sea. Postfixes on RiverGenerator.SampleSelf / SampleConnector cap only the bank factor this node added.
    /// </summary>
    public static class DryChannels
    {
        /// <summary>One capped node. Cap: its own bank-factor cap (NaN = the dry cap); shallow deltas use a cap a few blocks below the sea.</summary>
        public sealed class Info
        {
            public bool FadeDownstream, RelaxUpstream, Groove; public Vector2d Start, End; public double Cap = double.NaN;
            /// <summary>Flight test 16: riffles along a relaxing stretch: amplitude (0..0.35 of the rise), waves along it, phase (radians).</summary>
            public double Amp, Waves = 1, Phase;
            /// <summary>Flight test 16b: this node's own groove depth (bank-factor units); NaN = no groove.</summary>
            public double GrooveBank = double.NaN;
            /// <summary>
            /// Flight test 17: where this node's Start lies along a relaxing stretch, as a fraction of the stretch
            /// (path distance past its start / its length). With one Info per node, Start..End along that node, the rise
            /// follows the river's path instead of one straight line (a bending river stayed shallow for kilometres).
            /// </summary>
            public double T0;
        }

        static double riverDepth = double.NaN, baseDepth = double.NaN;

        /// <summary>
        /// Groove depth at a column. Rivers' own bank profile (IL, SampleSelf / SampleConnector) is an ellipse,
        /// bank = max(sqrt(size) x riverDepth, baseDepth) x sqrt(1 - (d / size)^2), so the column's distance d from the
        /// channel's centre line follows from the uncapped bank value. A narrow round-bottomed groove (half-width 1.5 to 3.5
        /// blocks) runs along that centre line, deepest (the node's Info.GrooveBank) in the middle.
        /// </summary>
        public static double GrooveAt(double bank, double size, double rDepth, double bDepth, double grooveBank)
        {
            if (grooveBank <= 0 || size <= 0) return 0;
            double full = Math.Max(Math.Sqrt(size) * rDepth, bDepth);
            if (!(full > 0)) return 0;
            double q = Math.Clamp(bank / full, 0, 1);
            double d = size * Math.Sqrt(1 - q * q);
            double w = Math.Clamp(size * 0.22, 1.5, 3.5);
            if (d >= w) return 0;
            double u = d / w;
            return grooveBank * Math.Sqrt(1 - u * u);
        }

        static void ReadDepths(object generator)
        {
            if (!double.IsNaN(riverDepth) || generator == null) return;
            try
            {
                var t = generator.GetType();
                var fr = t.GetField("riverDepth", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var fb = t.GetField("baseDepth", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                double r = fr != null ? Convert.ToDouble(fr.GetValue(generator)) : 0, b = fb != null ? Convert.ToDouble(fb.GetValue(generator)) : 0;
                baseDepth = b; riverDepth = r;
            }
            catch (Exception) { baseDepth = 0; riverDepth = 0; }
        }

        static readonly ConditionalWeakTable<object, Info> nodes = new ConditionalWeakTable<object, Info>();
        static int count;
        static Func<object, object> segNode;
        public static double Cap = double.NaN;   // bank factor cap, set when the world generator starts

        public static void Bind(FieldInfo segmentNode)
        {
            var p = Expression.Parameter(typeof(object));
            segNode = Expression.Lambda<Func<object, object>>(
                Expression.Convert(Expression.Field(Expression.Convert(p, segmentNode.DeclaringType), segmentNode), typeof(object)), p).Compile();
        }

        public static void Register(object node, Info info)
        {
            if (node == null) return;
            nodes.AddOrUpdate(node, info);
            Interlocked.Increment(ref count);
        }

        /// <summary>Depth cap at a point: Cap, faded to 0 toward a dry river's mouth (smoothstep over the downstream 60%).</summary>
        public static double CapAt(Info info, Vector2d p)
        {
            double cap = double.IsNaN(info.Cap) ? Cap : info.Cap;
            if (!info.FadeDownstream && !info.RelaxUpstream) return cap;
            var ab = info.End - info.Start;
            double l2 = ab.X * ab.X + ab.Y * ab.Y;
            double t = l2 < 1e-9 ? 1 : Math.Clamp(info.T0 + ((p.X - info.Start.X) * ab.X + (p.Y - info.Start.Y) * ab.Y) / l2 / 0.6, 0, 1);
            double s = t * t * (3 - 2 * t);
            // Flight test 16: riffles, so the rise is not one even ramp. The wobble is zero at both ends (s(1 - s) x 4), so the
            // stretch still starts at the cap and ends at full depth.
            if (info.Amp > 0)
                s = Math.Clamp(s + Math.Min(0.35, info.Amp) * Math.Sin(2 * Math.PI * info.Waves * t + info.Phase) * s * (1 - s) * 4, 0, 1);
            // RelaxUpstream (shallow delta mouth): the cap holds at the downstream end and lifts over the node, so the river bed
            // rises gently into the flat delta instead of stepping up at the node boundary.
            return info.RelaxUpstream ? cap + (1.0 - cap) * s : cap * s;
        }

        static void Apply<T>(object generator, object segment, object incoming, object point, object size, ref T result)
        {
            if (count == 0 || segNode == null || segment == null) return;
            var node = segNode(segment);
            if (node == null || !nodes.TryGetValue(node, out var info)) return;
            double before = incoming is T inc ? Acc<T>.Bank(inc) : 0;
            double after = Acc<T>.Bank(result);
            if (after <= before) return;
            double cap = point is Vector2d v ? CapAt(info, v) : (double.IsNaN(info.Cap) ? Cap : info.Cap);
            if (double.IsNaN(cap)) return;
            double gb = double.IsNaN(info.GrooveBank) ? 0 : info.GrooveBank;
            if (info.Groove && gb > 0 && size is float sz)
            {
                ReadDepths(generator);
                cap += GrooveAt(after, sz, riverDepth, baseDepth, gb);
            }
            Acc<T>.SetBank(ref result, Math.Max(before, Math.Min(after, cap)));
        }

        // SampleSelf(segment, riverSample, point, riverSize, ref closestLine)
        public static void SelfPostfix<T>(object __instance, object[] __args, ref T __result) => Apply(__instance, __args[0], __args[1], __args[2], __args[3], ref __result);
        // SampleConnector(segment, connector, projection, riverSample, point, riverSize, ref closestLine)
        public static void ConnectorPostfix<T>(object __instance, object[] __args, ref T __result) => Apply(__instance, __args[0], __args[3], __args[4], __args[5], ref __result);

        /// <summary>Compiled field access for RiverSample (a struct only known at run time).</summary>
        public static class Acc<T>
        {
            public delegate void Setter(ref T s, double v);
            public static Func<T, double> Bank;
            public static Setter SetBank;

            public static void Init(FieldInfo bank)
            {
                var p = Expression.Parameter(typeof(T));
                Bank = Expression.Lambda<Func<T, double>>(Expression.Field(p, bank), p).Compile();
                var r = Expression.Parameter(typeof(T).MakeByRefType());
                var val = Expression.Parameter(typeof(double));
                SetBank = Expression.Lambda<Setter>(Expression.Assign(Expression.Field(r, bank), val), r, val).Compile();
            }
        }
    }
}
