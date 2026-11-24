using System;
using System.Collections.Generic;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace RiverFeatures
{
    /// <summary>
    /// /rfprobe [radius]: what the columns around the player actually hold, for diagnosing water artifacts (flight test 8).
    /// Per column: the top solid block, the water (top and bottom Y, block code at the top), empty cells between the water
    /// and the ground below it, whether River Features placed the water (PlacedWater mod data), the original height if
    /// River Features changed the column, and Rivers' flow vector. A summary lists the water block codes seen and every pair of
    /// orthogonally adjacent water columns whose surfaces differ (a water step). Read-only.
    /// </summary>
    public static class ProbeCommand
    {
        public static void Register(ICoreServerAPI api)
        {
            api.ChatCommands.Create("rfprobe")
                .WithDescription("River Features: list ground, water and flags of the columns around you (read-only diagnostics).")
                .RequiresPrivilege(Privilege.controlserver)
                .RequiresPlayer()
                .WithArgs(api.ChatCommands.Parsers.OptionalInt("radius", 2))
                .HandleWith(args =>
                {
                    try { return TextCommandResult.Success(Probe(api, args.Caller.Player.Entity.Pos.AsBlockPos, Math.Clamp((int)args[0], 0, 4))); }
                    catch (Exception e) { return TextCommandResult.Error("twprobe failed: " + e.Message); }
                });
        }

        sealed class Col { public int X, Z, Ground = -1, WTop = -1, WBot = -1, Empty, Orig = -1, PlanTop = -1, PlanSurf = -1; public string Code, GroundCode, Why = "-"; public bool Tw; public float Flow; }

        static string Probe(ICoreServerAPI api, BlockPos at, int r)
        {
            var ba = api.World.BlockAccessor;
            int yTop = Math.Min(api.WorldManager.MapSizeY - 2, at.Y + 24), yLow = Math.Max(1, at.Y - 40);
            var cols = new Dictionary<(int, int), Col>();
            var codes = new HashSet<string>();
            for (int dz = -r; dz <= r; dz++)
                for (int dx = -r; dx <= r; dx++)
                {
                    var c = new Col { X = at.X + dx, Z = at.Z + dz };
                    for (int y = yTop; y >= yLow; y--)
                    {
                        var fl = ba.GetBlock(c.X, y, c.Z, BlockLayersAccess.Fluid);
                        var so = ba.GetBlock(c.X, y, c.Z, BlockLayersAccess.Solid);
                        bool fluid = fl != null && fl.BlockId != 0, solid = so != null && so.BlockId != 0 && so.BlockMaterial != EnumBlockMaterial.Plant;
                        if (fluid && c.WTop < 0) { c.WTop = y; c.Code = fl.Code?.ToShortString(); codes.Add(c.Code); }
                        if (fluid) c.WBot = y;
                        if (solid) { c.Ground = y; c.GroundCode = so.Code?.ToShortString(); break; }
                        if (!fluid && c.WTop >= 0) c.Empty++;   // empty cell below the water surface
                    }
                    int lx = ((c.X % 32) + 32) % 32, lz = ((c.Z % 32) + 32) % 32, i = lz * 32 + lx;
                    var mc = ba.GetMapChunkAtBlockPos(new BlockPos(c.X, 1, c.Z));
                    var orig = OriginalHeights.Decode(mc?.GetModdata(ChunkCtx.OriginalHeightsKey));
                    if (orig != null) c.Orig = orig[i];
                    var placed = PlacedWater.Decode(mc?.GetModdata(ChunkCtx.PlacedWaterKey));
                    c.Tw = placed != null && placed[i];
                    var lv = LakeSeamDiag.Read(mc?.GetModdata(ChunkCtx.LakeViewKey), lx, lz);
                    if (lv.Known) c.Why = lv.Reason.ToString();
                    var plan = mc?.GetModdata(ChunkCtx.PlanKey);
                    if (plan != null && plan.Length == 4096)
                    {
                        int pt = plan[4 * i] | (plan[4 * i + 1] << 8), ps = plan[4 * i + 2] | (plan[4 * i + 3] << 8);
                        if (pt != 0xFFFF) { c.PlanTop = pt; c.PlanSurf = ps; }
                    }
                    var ch = ba.GetChunkAtBlockPos(new BlockPos(c.X, Math.Max(1, c.WTop), c.Z));
                    var flow = ch?.GetModdata<float[]>(FlowFix.ModDataKey, null);
                    if (flow != null && flow.Length == 2 * FlowFix.Cells) c.Flow = (float)Math.Sqrt(flow[i] * flow[i] + flow[1024 + i] * flow[1024 + i]);
                    cols[(dx, dz)] = c;
                }

            var sb = new StringBuilder();
            sb.Append("River Features probe at ").Append(at.X).Append(',').Append(at.Y).Append(',').Append(at.Z).Append(" (r ").Append(r).Append(")\n");
            sb.Append("x,z: ground [block] | water bot..top [code] | empty below surface | RF water | orig height | RF planned top/surface | flow | lake decision\n");
            for (int dz = -r; dz <= r; dz++)
                for (int dx = -r; dx <= r; dx++)
                {
                    var c = cols[(dx, dz)];
                    sb.Append(c.X).Append(',').Append(c.Z).Append(": ").Append(c.Ground).Append(" [").Append(c.GroundCode).Append("] | ");
                    if (c.WTop >= 0) sb.Append(c.WBot).Append("..").Append(c.WTop).Append(" [").Append(c.Code).Append(']'); else sb.Append("dry");
                    sb.Append(" | ").Append(c.Empty).Append(" | ").Append(c.Tw ? "yes" : "no").Append(" | ").Append(c.Orig).Append(" | ").Append(c.PlanTop < 0 ? "-" : c.PlanTop + "/" + c.PlanSurf).Append(" | ").Append(c.Flow.ToString("0.##")).Append(" | ").Append(c.Why).Append('\n');
                }
            int steps = 0;
            var stepList = new StringBuilder();
            for (int dz = -r; dz <= r; dz++)
                for (int dx = -r; dx <= r; dx++)
                    foreach (var (ox, oz) in new[] { (1, 0), (0, 1) })
                    {
                        if (!cols.TryGetValue((dx + ox, dz + oz), out var b)) continue;
                        var a = cols[(dx, dz)];
                        if (a.WTop < 0 || b.WTop < 0 || a.WTop == b.WTop) continue;
                        steps++;
                        if (steps <= 8) stepList.Append(' ').Append(a.X).Append(',').Append(a.Z).Append('=').Append(a.WTop).Append(" vs ").Append(b.X).Append(',').Append(b.Z).Append('=').Append(b.WTop).Append(';');
                    }
            sb.Append("water codes: ").Append(string.Join(", ", codes)).Append('\n');
            sb.Append("water steps between adjacent columns: ").Append(steps).Append(stepList);
            return sb.ToString();
        }
    }
}
