using System.Text;
using NaviDnD.Data.Models;
using NaviDnD.MapGen.Generators;

namespace NaviDnD;

// Travel on the world map (D&D 5e, "Travel"): a route on foot over world cells (a cell ≈ a quarter day by road, WorldAtlas.CellDays),
// pace (fast — farther but you notice less; slow — shorter but you can travel stealthily), camps with a long rest,
// random encounters by regional danger (a road within a kingdom is calm, the wilds more dangerous; hostile ones are rarer than
// peaceful ones and finds), navigation off the roads. The code handles the mechanics (path, time, encounter rolls); the DM, in one call,
// narrates the road and plays out the encounter (GameAiClient.Travel).
public static class TravelService
{
    public enum Pace { Slow, Normal, Fast }

    public static string PaceName(Pace p) => p switch { Pace.Fast => "fast", Pace.Slow => "slow", _ => "normal##m" };

    // Travel time for display (the legend): "2 days", "1.5 days" — in the interface language; the DM's summary is in English.
    public static string DaysLabel(double d) =>
        d % 1 != 0 ? L.F("{0:0.#} days", d) : $"{(int)d} {L.Plural((int)d, "day", "days")}";

    // Distance covered in the same time: fast — 30 miles a day instead of 24, slow — 18.
    private static double Speed(Pace p) => p switch { Pace.Fast => 1.25, Pace.Slow => 0.75, _ => 1.0 };

    // Food on the road: what food is in the inventory (id, quantity) — the DM deducts it with a patch; if short — foraging,
    // anything obtained beyond the need goes into the inventory (otherwise the gathered supply was lost).
    // Item names may be Russian (Russian worlds) or English.
    private static readonly System.Text.RegularExpressions.Regex FoodName = new(
        "рацион|паёк|паек|провизи|еда|пищ|вялен|сухар|хлеб|сыр|мяс|рыб|ягод|орех|фрукт|яблок|похлёбк|похлебк|дичь"
        + "|ration|provision|food|jerky|dried|hardtack|biscuit|bread|cheese|meat|fish|berr|nuts|fruit|apple|stew|venison",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static string FoodLine(WorldState ws, int days)
    {
        var inv = ws.Hero?.Inventory ?? [];
        var food = inv.Select((it, i) => (it, i))
            .Where(t => t.it.Deleted != true && t.it.Name is { } n && FoodName.IsMatch(n))
            .Select(t => $"{t.it.Name} (id {t.i}) ×{t.it.Quantity ?? 1}").ToList();
        // Food inside a kit ("Explorer's Pack: … 10 rations …") is food too: it gets split out as a separate item.
        var packed = inv.Select((it, i) => (it, i))
            .Where(t => t.it.Deleted != true && !FoodName.IsMatch(t.it.Name ?? "") && t.it.Description is { } d && FoodName.IsMatch(d))
            .Select(t => $"in \"{t.it.Name}\" (id {t.i}): {t.it.Description}").ToList();
        string have = food.Count > 0 ? string.Join(", ", food) : "no separate food";
        string inKits = packed.Count > 0 ? $" Food inside kits — {string.Join("; ", packed)}: split it out as a separate item with quantity (and remove it from the kit description), deduct what is eaten." : "";
        return $"Food: {days} {DayWord(days)} on the road — 1 day's ration per day. In the inventory: {have}.{inKits} "
               + "Deduct what is eaten with a hero.inventory patch ({\"id\":N,\"quantity\":remaining}, used up — deleted). If short — foraging: ONE roll_dice Survival DC 10 for the whole shortfall "
               + "(success — there was enough food, failure — half of the missing days go hungry, exhaustion per the rules); anything obtained beyond the need — put the surplus in the inventory as an item with quantity (\"Foraged food\"), do not lose it. "
               + "Do not call food that is present missing.";
    }

    public sealed record Plan((int x, int y) From, (int x, int y) To, string? TargetPlace, WorldAtlas.RouteInfo Route)
    {
        public double Days(Pace pace) => DaysAt(Route.Days, pace);
    }

    public static double DaysAt(double normalDays, Pace pace) => Math.Max(0.5, Math.Round(normalDays / Speed(pace) * 2) / 2);

    // The hero's route to a world cell (or a place on it).
    public static Plan? PlanTo(WorldState ws, (int x, int y) target)
    {
        if (GameWorld.HeroTile(ws) is not { } from || GameWorld.ForMaster(ws) is not { } world) return null;
        if (from == target) return null;
        var route = WorldAtlas.Route(world, from, target);
        if (route == null) return null;
        var place = world.Places.FirstOrDefault(p => p.X == target.x && p.Y == target.y && (!p.Hidden));
        return new Plan(from, target, place?.Name, route);
    }

    // Walk the route: the hero travels day by day, each day an encounter roll; if one comes up, the journey stops there.
    // Moves the hero and time, takes them out of the location; returns the summary for the DM.
    public static string Go(WorldState ws, Plan plan, Pace pace, Random rng)
    {
        var world = GameWorld.ForMaster(ws)!;
        var link = ws.World!;
        string fromName = link.Place ?? WorldAtlas.TileName(world, plan.From.x, plan.From.y);
        GameWorld.LeaveLocation(ws);
        // Events scheduled for a round of the scene the hero left: the round count resets on the road and days
        // pass — without cancelling, they would "fire" later out of place.
        var dropped = (ws.ScheduledEvents ?? []).Where(e => e.Deleted != true).Select(e => e.Name).ToList();
        ws.ScheduledEvents = [];

        // Path cells by day (a quarter day per cell by road, longer off road; pace changes the daily distance).
        var path = plan.Route.Path;
        double perDay = 1.0 * Speed(pace);
        // A cell's day follows the accumulated travel time (as in the route estimate): the hero travels as many days as
        // were shown when choosing the destination.
        var days = new List<List<(int x, int y)>>();
        double acc = 0;
        for (int i = 1; i < path.Count; i++)
        {
            double step = Math.Min(WorldAtlas.StepDays(world, path[i].x, path[i].y), 6 * WorldAtlas.CellDays);
            if (i == path.Count - 1) step = Math.Min(step, 1.5 * WorldAtlas.CellDays);   // as in WorldAtlas.Route
            int day = Math.Max(0, (int)Math.Ceiling((acc + step) / perDay - 1e-9) - 1);
            acc += step;
            while (days.Count <= day) days.Add([]);
            days[day].Add(path[i]);
        }
        days.RemoveAll(d => d.Count == 0);

        // Encounters: a daily chance by regional danger; the type is more often a peaceful encounter or a find, less often enemies.
        int stopDay = -1;
        string? encounter = null;
        for (int d = 0; d < days.Count && stopDay < 0; d++)
        {
            double chance = days[d].Max(t => Danger(world, t.x, t.y));
            if (pace == Pace.Slow) chance *= 0.8;
            if (rng.NextDouble() >= chance) continue;
            double r = rng.NextDouble();
            double hostile = pace == Pace.Slow ? 0.18 : 0.25;
            encounter = r < hostile ? "hostile (ambush, predator, bandits — scaled to the hero's strength, can be avoided or negotiated)"
                : r < hostile + 0.45 ? "peaceful (travellers, a merchant, a patrol, a hermit — conversation, rumours, a request)"
                : "a find (tracks, ruins, a cache, a sign, an unusual spot — a hook, a place can be added to the map)";
            stopDay = d;
        }

        int walked = stopDay >= 0 ? stopDay + 1 : days.Count;
        var walkedTiles = days.Take(walked).SelectMany(x => x).ToList();
        var end = walkedTiles.Count > 0 ? walkedTiles[^1] : plan.From;
        bool arrived = stopDay < 0;

        // Time: set off in the morning (in the evening/at night — the next morning), a travel day lasts until evening, camp, night.
        int startDay = ws.Time.Day + (ws.Time.PartOfDay is PartsOfDay.Evening or PartsOfDay.Night ? 1 : 0);
        ws.Time.Day = startDay + walked - 1;
        ws.Time.PartOfDay = PartsOfDay.Evening;
        ws.Time.TotalRounds = 1;

        link.X = end.x; link.Y = end.y;
        var arrivedPlace = arrived && plan.TargetPlace != null ? WorldAtlas.FindPlace(world, plan.TargetPlace) : null;
        link.Place = arrivedPlace?.Name;
        if (arrivedPlace != null && !link.Visited.Contains(arrivedPlace.Name)) link.Visited.Add(arrivedPlace.Name);
        link.Version++;

        // Summary for the DM.
        var sb = new StringBuilder();
        sb.AppendLine("## TRAVEL (the engine has already moved the hero on the world map and advanced time — do not change time or world.place)");
        string toName = plan.TargetPlace ?? WorldAtlas.TileName(world, plan.To.x, plan.To.y);
        sb.AppendLine($"From: {fromName} → to: {toName}, {L.Strip(WorldAtlas.Direction(plan.To.x - plan.From.x, plan.To.y - plan.From.y))} (this is the only direction of travel). Pace: {L.Strip(PaceName(pace))}{(pace == Pace.Fast ? " (−5 to passive Perception on the road)" : pace == Pace.Slow ? " (can travel stealthily)" : "")}.");
        double roadShare = walkedTiles.Count == 0 ? 1 : walkedTiles.Count(t => world.RoadAt(t.x, t.y) > 0) / (double)walkedTiles.Count;
        sb.AppendLine($"On the road: {walked} {DayWord(walked)} of {days.Count}, {roadShare:P0} by road; terrain: {Terrains(world, walkedTiles)}.");
        var rivers = Rivers(world, walkedTiles);
        if (rivers.Length > 0) sb.AppendLine($"Rivers: {rivers}.");
        var kingdoms = walkedTiles.Select(t => world.OwnerAt(t.x, t.y)).Distinct().Select(o => o >= 0 && o < world.Kingdoms.Count ? world.Kingdoms[o].Name : "no man's land").ToList();
        sb.AppendLine($"Lands: {string.Join(" → ", kingdoms.Distinct())}.");
        var near = world.Places.Where(p => walkedTiles.Any(t => Math.Abs(t.x - p.X) <= 2 && Math.Abs(t.y - p.Y) <= 2)
                && p.Name != plan.TargetPlace && p.Name != fromName)
            .Select(p => $"{p.Name} ({L.Strip(WorldPlaceTypes.Label(p.Type))}{(p.Hidden ? ", unknown to the hero" : "")})").ToList();
        if (near.Count > 0) sb.AppendLine($"Near the road: {string.Join(", ", near.Take(6))}.");
        int nights = Math.Max(walked - 1, 0);
        if (dropped.Count > 0)
            sb.AppendLine($"Scheduled events of the previous scene were cancelled by the engine (the hero left, days passed): {string.Join("; ", dropped)} — if they matter, account for their outcome in the narration.");
        sb.AppendLine($"Camps: nights on the road — {nights}; on arrival/at camp — a long rest (long_rest) unless interrupted.");
        sb.AppendLine(FoodLine(ws, walked));
        if (roadShare < 0.5) sb.AppendLine("More than half the journey is off road: navigation check — roll_dice Survival DC 10 (forest/hills) … 15 (mountains/swamp/desert); failure — lost the way, +1 day (time.day+1), describe the wandering.");
        if (encounter != null)
            sb.AppendLine($"ENCOUNTER on day {walked}, {L.Strip(WorldBiomes.Get(world.BiomeAt(end.x, end.y)).Name).ToLowerInvariant()} ({WorldAtlas.Surroundings(world, end.x, end.y, named: true)}): {encounter}. The journey stops here — play out the encounter to fit the place and kingdom; if a tactical scene is needed (combat, searching ruins) — plan_location (the engine fills in the cell's terrain itself), otherwise in text. Afterwards the hero can continue the journey on their own.");
        else if (arrivedPlace != null)
        {
            bool saved = GameWorld.Geo(ws) is { } geo && GameWorld.SavedLocationJson(geo, arrivedPlace.Name) != null;
            sb.AppendLine($"Arrival: {arrivedPlace.Name} — {L.Strip(WorldPlaceTypes.Label(arrivedPlace.Type))}.{(arrivedPlace.Description is { Length: > 0 } ad ? " " + ad : "")}"
                          + (saved ? " The hero has been here before: the engine opened the saved location — the hero is outside by the entrance; for lodging or going inside — describe it and move the hero with a hero.position patch." : " There is no tactical map — run the scene in text; if one is needed (combat, dungeon, exploration) — plan_location."));
        }
        else sb.AppendLine($"Arrival: the hero has reached the spot — {L.Strip(WorldBiomes.Get(world.BiomeAt(end.x, end.y)).Name).ToLowerInvariant()}, {WorldAtlas.Surroundings(world, end.x, end.y, named: true)}. Where there is no place on the map, something can be found (add_place) or the hero can simply make camp.");
        sb.AppendLine("Response: a short account of the journey (2–4 history entries, without retelling every day); food/rest via patch.");
        return sb.ToString();
    }

    // Regional danger: the chance of an encounter per travel day.
    private static double Danger(WorldMap w, int x, int y)
    {
        bool road = w.RoadAt(x, y) > 0, owned = w.OwnerAt(x, y) >= 0;
        double d = road && owned ? 0.05 : owned ? 0.09 : 0.14;
        if (w.BiomeAt(x, y) is WorldBiomes.Swamp or WorldBiomes.Mountains or WorldBiomes.DeepForest or WorldBiomes.Peaks) d += 0.03;
        return d;
    }

    private static string Terrains(WorldMap w, List<(int x, int y)> tiles) =>
        tiles.Count == 0 ? "—" : string.Join(", ", tiles.GroupBy(t => L.Strip(WorldBiomes.Get(w.BiomeAt(t.x, t.y)).Name).ToLowerInvariant())
            .OrderByDescending(g => g.Count()).Take(4).Select(g => g.Key));

    private static string Rivers(WorldMap w, List<(int x, int y)> tiles)
    {
        int bridges = tiles.Count(t => w.RiverAt(t.x, t.y) > 0 && w.RoadAt(t.x, t.y) > 0);
        int fords = tiles.Count(t => w.RiverAt(t.x, t.y) > 0 && w.RoadAt(t.x, t.y) == 0);
        var parts = new List<string>();
        if (bridges > 0) parts.Add($"bridges {bridges}");
        if (fords > 0) parts.Add($"fords {fords} (crossing — Athletics DC 10 or go around)");
        return string.Join(", ", parts);
    }

    private static string DayWord(int d) =>
        d == 1 ? "day" : "days";
}
