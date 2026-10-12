using System.Text;
using NaviDnD.Data.Models;
using NaviDnD.Helpers;
using NaviDnD.MapGen.Generators;

namespace NaviDnD.Display;

// Draws the world map ([F2]) from WorldMap. Zoom levels (DisplayConfig.WorldZoomStep):
//   "pixels" are ▄ half-blocks: one character = 2 pixels stacked vertically (lower = character color, upper = background);
//   0 is the closest: a world cell = 4×4 pixels, 1 = 2×2; beyond that a pixel = s×s world cells (s = scale),
//   colored by the dominant terrain with hillshading.
// On top: rivers, roads, place icons and labels (settlements in their kingdom's color) by priority (they don't overlap
// each other; small places only when zoomed in). The frame is computed whole and cached.
public sealed class WorldMapView
{
    public const int MinZoom = 0;

    private readonly WorldMap _w;
    public WorldMap World => _w;

    // Zoom levels (world cells per half-block pixel): ¼ (cell = 4×4 pixels), ½ (2×2), 1, 2, … and the last one is "world fits the
    // window" (fractional: the world exactly fills the map area and zooms out no further).
    private readonly List<double> _scales;
    public int MaxZoom => _scales.Count - 1;
    public double Scale(int zoom) => _scales[Math.Clamp(zoom, 0, MaxZoom)];

    public WorldMapView(WorldMap world, int widthChars, int heightRows)
    {
        _w = world;
        double fit = Math.Min(world.Width / (double)widthChars, world.Height / (heightRows * 2.0));
        _scales = [0.25, 0.5];
        for (int s = 1; s < fit - 0.15; s++) _scales.Add(s);
        if (fit > _scales[^1]) _scales.Add(fit);
    }

    // ── Frame ──
    private sealed record Frame(string[] Rows, Dictionary<(int col, int row), int> PlaceAt, HashSet<int> TextRows);

    // Frame rows with labels (of the last built frame): MapDisplay redraws those rows a second time.
    public IReadOnlyCollection<int> TextRows => _frame?.TextRows ?? (IReadOnlyCollection<int>)[];
    private Frame? _frame;
    private (double cx, double cy, int z, int w, int h, int hover) _frameKey;
    private string _filterKey = "";

    // Legend filter categories: key, label, hotkey.
    public static (string key, string label, string fkey)[] Categories =>
        [("settlements", L.T("Settlements"), "F7"), ("sites", L.T("Adventures"), "F8"), ("nature", L.T("Nature"), "F9")];

    // Fortresses go with settlements.
    public static string Category(string type) => type switch
    {
        WorldPlaceTypes.Ruins or WorldPlaceTypes.Dungeon or WorldPlaceTypes.Cave or WorldPlaceTypes.Shrine => "sites",
        _ => "settlements",
    };

    private HashSet<string> _filter = [.. Categories.Select(c => c.key)];
    private int _selected = -1;

    // The hero on the world map (cell, color) and the plotted route to the destination (cells), drawn over the terrain.
    public (int x, int y)? Hero { get; set; }
    public List<int> HeroColor { get; set; } = [255, 255, 255];

    // The hero (and the start marker during game creation) is drawn in purple half-block pixels, the path in bright pink:
    // the map has no such colors. MarkerGlyph is the hero's icon in the legend.
    public const char MarkerGlyph = '■';
    public static readonly List<int> MarkerColor = [255, 40, 190];   // bright pink, opaque: brighter than the semi-transparent path
    // Smoothed drawing of the path through the route cells (cell centers): a "pulled string" from a point to the farthest one
    // such that every intermediate cell lies within a cell of the straight line (cell staircases and L-corners go away),
    // then two Chaikin passes for smooth turns. The ends sit exactly at the start and the destination.
    private static List<(double x, double y)> SmoothRoute(List<(int x, int y)> route)
    {
        var mid = route.Select(t => (x: t.x + 0.5, y: t.y + 0.5)).ToList();
        var keep = new List<(double x, double y)> { mid[0] };
        int i = 0;
        while (i < mid.Count - 1)
        {
            int j = i + 1;
            while (j + 1 < mid.Count && Fits(i, j + 1)) j++;
            keep.Add(mid[j]);
            i = j;
        }
        bool Fits(int a, int b)
        {
            var (ax, ay) = mid[a];
            var (bx, by) = mid[b];
            double len = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
            if (len < 1e-9) return true;
            for (int t = a + 1; t < b; t++)
            {
                var (px, py) = mid[t];
                double d = Math.Abs((bx - ax) * (ay - py) - (ax - px) * (by - ay)) / len;   // distance to the line
                if (d > 1.0) return false;
            }
            return true;
        }
        for (int pass = 0; pass < 2 && keep.Count > 2; pass++)
        {
            var next = new List<(double x, double y)> { keep[0] };
            for (int t = 0; t < keep.Count - 1; t++)
            {
                var (ax, ay) = keep[t];
                var (bx, by) = keep[t + 1];
                if (t > 0) next.Add((0.75 * ax + 0.25 * bx, 0.75 * ay + 0.25 * by));
                if (t < keep.Count - 2) next.Add((0.25 * ax + 0.75 * bx, 0.25 * ay + 0.75 * by));
            }
            next.Add(keep[^1]);
            keep = next;
        }
        return keep;
    }

    public static readonly List<int> RouteColor = [255, 60, 200];

    public List<(int x, int y)>? Route { get; set; }
    private string _overlayKey = "";
    private string OverlayKey => $"{Hero}|{Route?.Count}|{(Route is { Count: > 0 } r ? r[^1] : default)}|{string.Join(",", HeroColor)}";

    public string RenderRow(int row, int widthChars, int heightRows, double cx, double cy, int zoom, int hoveredPlace,
        int selectedPlace = -1, HashSet<string>? filter = null)
    {
        string filterKey = filter == null ? "*" : string.Join(",", filter.OrderBy(f => f));
        var key = (cx, cy, zoom, widthChars, heightRows, hoveredPlace);
        if (_frame == null || _frameKey != key || _selected != selectedPlace || _filterKey != filterKey || _overlayKey != OverlayKey)
        {
            _overlayKey = OverlayKey;
            _selected = selectedPlace;
            _filter = filter ?? [.. Categories.Select(c => c.key)];
            _filterKey = filterKey;
            _frame = Build(widthChars, heightRows, cx, cy, zoom, hoveredPlace);
            _frameKey = key;
        }
        return row >= 0 && row < _frame.Rows.Length ? _frame.Rows[row] : "";
    }

    // Places whose icons are visible in the window at this zoom (for the legend), by priority.
    public List<int> VisiblePlaces(int W, int H, double cx, double cy, int z, HashSet<string> filter)
    {
        var list = new List<int>();
        for (int i = 0; i < _w.Places.Count; i++)
        {
            var p = _w.Places[i];
            if (!VisibleAt(p.Type, Stage(z)) || !filter.Contains(Category(p.Type))) continue;
            double sc = Scale(z);
            double col = (p.X - cx) / sc + W / 2;
            double row = ((p.Y - cy) / sc + H) / 2;
            if (col >= 0 && col < W && row >= 0 && row < H) list.Add(i);
        }
        return list.OrderBy(i => Priority(_w.Places[i].Type)).ThenBy(i => _w.Places[i].Name).ToList();
    }

    // Place under a screen character (icon or label), for hover.
    public int PlaceAtCell(int col, int row) => _frame != null && _frame.PlaceAt.TryGetValue((col, row), out int p) ? p : -1;

    // World cell under a screen character.
    public (int x, int y) TileAt(int col, int row, int widthChars, int heightRows, double cx, double cy, int zoom)
    {
        double s = Scale(zoom);
        return ((int)Math.Floor(cx + (col - widthChars / 2) * s), (int)Math.Floor(cy + (2 * row + 1 - heightRows) * s));
    }

    private Frame Build(int W, int H, double cx, double cy, int z, int hovered)
    {
        var ch = new char[H, W];
        var fg = new List<int>[H, W];
        var bg = new List<int>[H, W];
        var placeAt = new Dictionary<(int, int), int>();
        var textRows = new HashSet<int>();

        // A half-block pixel = s×s world cells (s = Scale(z)); at the closest zoom s = ½: a world cell
        // takes 2×2 pixels (2 characters × 1 row), a more detailed picture, also in half-blocks.
        double s = Scale(z);
        int block = Math.Max(1, (int)Math.Round(s));
        for (int r = 0; r < H; r++)
            for (int c = 0; c < W; c++)
            {
                int tx = (int)Math.Floor(cx + (c - W / 2) * s);
                int ty0 = (int)Math.Floor(cy + (2 * r - H) * s), ty1 = (int)Math.Floor(cy + (2 * r + 1 - H) * s);
                // ▄: the character color is the lower pixel, the background the upper one.
                if (s < 1)
                {
                    // Zoomed in: a world cell is k×k pixels, each with its own shade (forest canopies, mountain ridges, streams).
                    int k = (int)Math.Round(1 / s);
                    int px = (int)Math.Floor(k * cx + (c - W / 2)), py0 = (int)Math.Floor(k * cy + (2 * r - H)), py1 = py0 + 1;
                    bg[r, c] = FinePixel(px, py0, k);
                    fg[r, c] = FinePixel(px, py1, k);
                }
                else
                {
                    bg[r, c] = PixelColor(tx, ty0, block);
                    fg[r, c] = PixelColor(tx, ty1, block);
                }
                // Half-block ▄ (not ▀: with ▀ the console font showed colored vertical stripes at the right edge
                // of every character). Near-identical top and bottom colors merge into a space with the average background.
                if (ColorDistance(fg[r, c], bg[r, c]) < 60) { bg[r, c] = ColorHelper.MixWith(fg[r, c], bg[r, c], 0.5); ch[r, c] = ' '; }
                else ch[r, c] = '▄';
            }

        // ── Places and labels ──
        var used = new bool[H, W];
        (int col, int row)? Screen(int x, int y)
        {
            double sc = Scale(z);
            double col = (x + (sc < 1 ? 0.5 : 0) - cx) / sc + W / 2;
            double row = ((y + (sc < 1 ? 0.5 : 0) - cy) / sc + H) / 2;
            int c = (int)Math.Floor(col), r = (int)Math.Floor(row);
            return c >= 0 && c < W && r >= 0 && r < H ? (c, r) : null;
        }
        bool Free(int r, int c0, int len) { for (int c = c0; c < c0 + len; c++) if (c < 0 || c >= W || used[r, c]) return false; return true; }
        // A label sits on a flat plate of one color (the average terrain color under it, slightly darker), padded by an empty cell
        // of plate at each end if it's free (the padding takes no space: an icon can still go on top of it).
        void Text(int r, int c0, string text, List<int> color, int place, double plate = 0.3)
        {
            int sr = 0, sg = 0, sb = 0;
            for (int i = 0; i < text.Length; i++)
            {
                var a = bg[r, c0 + i]; var b = fg[r, c0 + i];
                sr += a[0] + b[0]; sg += a[1] + b[1]; sb += a[2] + b[2];
            }
            int n = Math.Max(1, text.Length * 2);
            var back = ColorHelper.MixWith([sr / n, sg / n, sb / n], [16, 18, 22], plate);
            void Cell(int c, char t, List<int> f)
            {
                ch[r, c] = t;
                fg[r, c] = f;
                bg[r, c] = back;
            }
            for (int i = 0; i < text.Length; i++)
            {
                int c = c0 + i;
                Cell(c, text[i], color);
                used[r, c] = true;
                if (place >= 0) placeAt[(c, r)] = place;
            }
            foreach (int c in new[] { c0 - 1, c0 + text.Length })
                if (c >= 0 && c < W && !used[r, c]) Cell(c, ' ', back);
            textRows.Add(r);
        }

        // Kingdom names: large, centered over their lands (when zoomed out).
        int st = Stage(z);
        if (st == 0)   // kingdom names only at the farthest zoom (the whole world)
            for (int k = 0; k < _w.Kingdoms.Count; k++)
            {
                var center = KingdomCenter(k);
                if (center is not var (kx, ky) || Screen(kx, ky) is not var (kc, kr)) continue;
                string label = Spaced(_w.Kingdoms[k].Name.ToUpperInvariant());
                int c0 = kc - label.Length / 2;
                if (Free(kr, c0, label.Length)) Text(kr, c0, label, ColorHelper.Pale(_w.Kingdoms[k].Color, 0.45), -1, 0.2);
            }

        // A half-block pixel (column c, pixel row p: upper = character background, lower = its color) in its own color.
        // alpha < 1 is semi-transparent: the terrain shows through.
        void SetPixel(int c, int p, List<int> color, bool overText = false, double alpha = 1)
        {
            if (c < 0 || c >= W || p < 0 || p >= 2 * H) return;
            int r = p / 2;
            if (used[r, c] && !overText) return;   // the path doesn't cut through (kingdom) labels
            if (ch[r, c] != '▄') { fg[r, c] = bg[r, c]; ch[r, c] = '▄'; }   // a character merged into a space becomes a half-block again
            var under = p % 2 == 0 ? bg[r, c] : fg[r, c];
            var col = alpha >= 1 ? color : ColorHelper.MixWith(under, color, alpha);
            if (p % 2 == 0) bg[r, c] = col; else fg[r, c] = col;
        }
        // A world cell in pixels: when zoomed in (cell = k×k pixels), a centered square covering a fraction frac of its
        // pixels; when zoomed out (a pixel = several cells), the pixel it falls into (wholeChar = the whole character).
        void PaintTile(int x, int y, List<int> color, double frac, bool wholeChar = false, bool overText = false, double alpha = 1)
        {
            if (s < 1)
            {
                int k = (int)Math.Round(1 / s), n = Math.Max(1, (int)Math.Round(k * frac)), off = (k - n) / 2;
                int c0 = x * k - (int)Math.Floor(k * cx) + W / 2, p0 = y * k - (int)Math.Floor(k * cy) + H;
                for (int dy = 0; dy < n; dy++)
                    for (int dx = 0; dx < n; dx++) SetPixel(c0 + off + dx, p0 + off + dy, color, overText, alpha);
                return;
            }
            // The same screen cell as place icons (Screen): otherwise the hero stood one character to the right of his town.
            int c = (int)Math.Floor((x - cx) / s + W / 2), p = (int)Math.Floor((y - cy) / s + H);
            if (wholeChar) { SetPixel(c, p - p % 2, color, overText, alpha); SetPixel(c, p - p % 2 + 1, color, overText, alpha); }
            else SetPixel(c, p, color, overText, alpha);
        }

        // The route to the destination in bright pink pixels along the path cells (under place icons).
        // The path to the destination is dotted: every other half-block pixel, pink and nearly transparent (terrain shows through).
        // The drawing is smoothed (SmoothRoute): near-straight stretches become one line, turns are smooth, with no staircases or
        // right angles along cells; the travel route itself doesn't change.
        if (Route is { Count: > 1 } route)
        {
            const double routeAlpha = 0.35;
            int k = s < 1 ? (int)Math.Round(1 / s) : 1;
            // World point (cell center is x+0.5) → the screen pixel at the same point in the cell as the hero icon
            // (PaintTile: pixel (k−1)/2 inside the cell; zoomed out, the cell's own pixel): otherwise the path started
            // one pixel to the side of the hero.
            (double c, double p) ToPixel((double x, double y) t) => s < 1
                ? (t.x * k - 0.5 - Math.Floor(k * cx) + W / 2, t.y * k - 0.5 - Math.Floor(k * cy) + H)
                : ((t.x - 0.5 - cx) / s + W / 2, (t.y - 0.5 - cy) / s + H);
            var pts = new List<(double c, double p)>();
            if (s >= 1) pts = SmoothRoute(route).Select(ToPixel).ToList();
            else
            {
                // Zoomed in: along road cells, follow the road's own geometry (cell center, exits to neighbors on the path,
                // a turn as a segment between the exits, like OnThinLine) so the dots land exactly on its pixels;
                // stretches without a road use the smoothed line.
                int m = (k - 1) / 2;
                (double, double) Px(int x, int y, int sx, int sy) =>
                    (x * k - Math.Floor(k * cx) + W / 2 + sx, y * k - Math.Floor(k * cy) + H + sy);
                (int sx, int sy) Exit(int dx, int dy) => dx > 0 ? (k - 1, m) : dx < 0 ? (0, m) : dy > 0 ? (m, k - 1) : (m, 0);
                int i = 0;
                while (i < route.Count)
                {
                    var (x, y) = route[i];
                    if (_w.RoadAt(x, y) > 0)
                    {
                        bool hasPrev = i > 0, hasNext = i < route.Count - 1;
                        var toPrev = hasPrev ? (route[i - 1].x - x, route[i - 1].y - y) : (0, 0);
                        var toNext = hasNext ? (route[i + 1].x - x, route[i + 1].y - y) : (0, 0);
                        bool turn = hasPrev && hasNext && (toPrev.Item1 == 0) != (toNext.Item1 == 0);
                        if (hasPrev) { var e = Exit(toPrev.Item1, toPrev.Item2); pts.Add(Px(x, y, e.sx, e.sy)); }
                        if (!turn) pts.Add(Px(x, y, m, m));
                        if (hasNext) { var e = Exit(toNext.Item1, toNext.Item2); pts.Add(Px(x, y, e.sx, e.sy)); }
                        i++;
                        continue;
                    }
                    int j = i;
                    while (j + 1 < route.Count && _w.RoadAt(route[j + 1].x, route[j + 1].y) == 0) j++;
                    pts.AddRange(SmoothRoute(route.GetRange(i, j - i + 1)).Select(ToPixel));
                    i = j + 1;
                }
            }
            int dot = 1;   // running counter so the dotting doesn't break at segment joins; starting at 1 leaves an empty half-block at the hero
            (int c, int p)? last = null;
            for (int i = 1; i < pts.Count; i++)
            {
                var (c0, p0) = pts[i - 1];
                var (c1, p1) = pts[i];
                int n = Math.Max(1, (int)Math.Ceiling(Math.Max(Math.Abs(c1 - c0), Math.Abs(p1 - p0))));
                for (int j = 1; j <= n; j++)
                {
                    var px = ((int)Math.Floor(c0 + (c1 - c0) * j / n), (int)Math.Floor(p0 + (p1 - p0) * j / n));
                    if (px == last) continue;
                    last = px;
                    if (dot++ % 2 == 0) SetPixel(px.Item1, px.Item2, RouteColor, alpha: routeAlpha);
                }
            }
        }

        var order = Enumerable.Range(0, _w.Places.Count).OrderBy(i => i == _selected ? -1 : Priority(_w.Places[i].Type)).ToList();
        foreach (int i in order)
        {
            var p = _w.Places[i];
            bool selected = i == _selected;
            if (!_filter.Contains(Category(p.Type)) && !selected) continue;
            if ((!VisibleAt(p.Type, st) && !selected) || Screen(p.X, p.Y) is not var (pc, pr) || used[pr, pc]) continue;
            var (icon, color) = Icon(p.Type);
            if (i == hovered || selected) color = ColorHelper.Pale(color, 0.6);
            ch[pr, pc] = icon;
            fg[pr, pc] = color;
            if (selected) bg[pr, pc] = ColorHelper.MixWith(bg[pr, pc], [255, 220, 120], 0.55);
            used[pr, pc] = true;
            placeAt[(pc, pr)] = i;
            // A settlement drawn on the map (wall, houses): hovering any part of it counts; the label goes outside the wall.
            int art = s < 1 ? ArtRadius(p.Type, (int)Math.Round(1 / s)) : 0;
            for (int dr = -art / 2; dr <= art / 2; dr++)
                for (int dc = -art; dc <= art; dc++)
                {
                    int ar = pr + dr, ac = pc + dc;
                    if (ar < 0 || ar >= H || ac < 0 || ac >= W || used[ar, ac] || dc * dc + 4 * dr * dr > art * art) continue;
                    used[ar, ac] = true;
                    placeAt[(ac, ar)] = i;
                }
            if (!LabelAt(p.Type, st) && !selected) continue;
            string name = selected ? $"«{p.Name}»" : p.Name;
            var labelColor = i == hovered || selected ? Bright : PlaceLabelColor(p);
            if (Free(pr, pc + art + 1, name.Length + 1)) Text(pr, pc + art + 1, " " + name, labelColor, i);
            else if (Free(pr, pc - art - name.Length - 1, name.Length + 1)) Text(pr, pc - art - name.Length - 1, name + " ", labelColor, i);
        }

        // The hero (and the start marker in the new-game wizard) goes on top of everything, exactly one half-block pixel at any zoom:
        // zoomed in, the center of his cell (previously a half-cell square, a whole character); zoomed out, his pixel.
        if (Hero is { } hero && Screen(hero.x, hero.y) is var (hc, hr))
        {
            PaintTile(hero.x, hero.y, HeroColor, 0.01, overText: true);
            used[hr, hc] = true;
        }

        // Natural regions: dimmed, if there's room.
        if (st >= 1 && _filter.Contains("nature"))
            foreach (var f in _w.Features.OrderByDescending(f => f.Size))
            {
                if (st == 1 && f.Size < (f.Biome == WorldGenerator.IslandCode ? 8 : _w.Size == WorldSizes.Large ? 120 : 60)) continue;
                if (Screen(f.X, f.Y) is not var (fc, fr)) continue;
                int c0 = fc - f.Name.Length / 2;
                if (Free(fr, c0, f.Name.Length)) Text(fr, c0, f.Name, FeatureColor(f.Biome), -1);
            }

        var rows = new string[H];
        for (int r = 0; r < H; r++)
        {
            var sb = new StringBuilder(W * 8);
            List<int>? lastFg = null, lastBg = null;
            for (int c = 0; c < W; c++)
            {
                if (!ReferenceEquals(bg[r, c], lastBg) && (lastBg == null || !lastBg.SequenceEqual(bg[r, c])))
                { var b = bg[r, c]; sb.Append("\x1b[48;2;").Append(b[0]).Append(';').Append(b[1]).Append(';').Append(b[2]).Append('m'); lastBg = b; }
                if (!ReferenceEquals(fg[r, c], lastFg) && (lastFg == null || !lastFg.SequenceEqual(fg[r, c])))
                { var f = fg[r, c]; sb.Append("\x1b[38;2;").Append(f[0]).Append(';').Append(f[1]).Append(';').Append(f[2]).Append('m'); lastFg = f; }
                sb.Append(ch[r, c]);
            }
            rows[r] = sb.ToString();
        }
        return new Frame(rows, placeAt, textRows);
    }

    private static int ColorDistance(List<int> a, List<int> b) =>
        Math.Abs(a[0] - b[0]) + Math.Abs(a[1] - b[1]) + Math.Abs(a[2] - b[2]);

    private static string Spaced(string s) => string.Join(" ", s.ToCharArray());

    private (int x, int y)? KingdomCenter(int k)
    {
        _kingdomCenters ??= ComputeKingdomCenters();
        return _kingdomCenters.TryGetValue(k, out var v) ? v : null;
    }

    private Dictionary<int, (int, int)>? _kingdomCenters;

    private Dictionary<int, (int, int)> ComputeKingdomCenters()
    {
        var sums = new Dictionary<int, (long sx, long sy, int n)>();
        for (int y = 0; y < _w.Height; y++)
            for (int x = 0; x < _w.Width; x++)
            {
                int o = _w.OwnerAt(x, y);
                if (o < 0) continue;
                var (sx, sy, n) = sums.GetValueOrDefault(o);
                sums[o] = (sx + x, sy + y, n + 1);
            }
        return sums.ToDictionary(kv => kv.Key, kv => ((int)(kv.Value.sx / kv.Value.n), (int)(kv.Value.sy / kv.Value.n)));
    }

    // ── Cell colors ──
    private static readonly List<int> RiverColor = [72, 122, 178];
    private static readonly List<int> RoadMain = [176, 148, 102];
    private static readonly List<int> RoadPath = [146, 128, 96];
    private static readonly List<int> BridgeColor = [122, 84, 50];
    private static readonly List<int> LabelColor = [236, 230, 210];

    // Color of a place's label on the map (and of the picture in its card): affiliation; a settlement/fortress name
    // in its kingdom's color.
    public List<int> PlaceLabelColor(WorldPlace p) =>
        p.Kingdom >= 0 && p.Kingdom < _w.Kingdoms.Count && (WorldPlaceTypes.IsSettlement(p.Type) || p.Type == WorldPlaceTypes.Castle)
            ? ColorHelper.Pale(_w.Kingdoms[p.Kingdom].Color, 0.45)
            : WorldPlaceTypes.IsSettlement(p.Type) ? LabelColor : SiteLabelColor;
    private static readonly List<int> SiteLabelColor = [214, 160, 150];
    private static readonly List<int> Bright = [255, 248, 200];

    private List<int> TileColor(int x, int y)
    {
        char b = _w.BiomeAt(x, y);
        var biome = WorldBiomes.Get(b);
        if (!_w.InBounds(x, y)) return biome.Color;
        var col = biome.Color;
        if (!biome.Water)
        {
            // Hillshading: slopes facing northwest are lighter.
            int dh = _w.HeightAt(x, y) - _w.HeightAt(x - 1, y - 1);
            double f = 1 + 0.07 * dh + 0.035 * (Hash(x, y) - 0.5);
            col = [Math.Clamp((int)(col[0] * f), 0, 255), Math.Clamp((int)(col[1] * f), 0, 255), Math.Clamp((int)(col[2] * f), 0, 255)];
        }
        else if (b == WorldBiomes.Ocean)
            col = ColorHelper.MixWith(col, [18, 30, 60], 0.15 * Hash(x / 3, y / 3));
        return col;
    }

    // A pixel at the close zooms (cell = k×k pixels, k = 2 or 4): the cell color plus terrain texture.
    private List<int> FinePixel(int px, int py, int k)
    {
        int x = FloorDiv(px, k), y = FloorDiv(py, k), sx = px - x * k, sy = py - y * k;
        int road = _w.RoadAt(x, y);
        bool onLine = road > 0 && OnRoadLine(x, y, sx, sy, k);
        int river = _w.RiverAt(x, y);
        // A settlement up close: wall, houses, streets (a river flows through it).
        if (TownPixel(px, py, k) is { } town && !(river >= 1 && InRiverBed(x, y, sx, sy, k, river))) return town;
        if (river >= 1)
        {
            // Bridge: a road across a river, along the same line, in the deck color.
            if (onLine) return BridgeColor;
            if (InRiverBed(x, y, sx, sy, k, river)) return RiverColor;
            return LandPixel(x, y, px, py, sx, sy, k);
        }
        if (onLine) return road == 2 ? RoadMain : ColorHelper.MixWith(TileColor(x, y), RoadPath, 0.6);
        // At k = 4 terrain borders are ragged: pixels near a cell edge sometimes take the neighbor's terrain (not river water).
        if (k >= 4)
        {
            double e = Hash(px * 31 + 7, py * 17 + 11);
            int nx = sx == 0 ? -1 : sx == k - 1 ? 1 : 0, ny = sy == 0 ? -1 : sy == k - 1 ? 1 : 0;
            if ((nx != 0 || ny != 0) && e < 0.4)
            {
                int ox = x + nx, oy = y + ny;
                if (_w.InBounds(ox, oy) && _w.BiomeAt(ox, oy) != _w.BiomeAt(x, y) && _w.RiverAt(ox, oy) == 0 && _w.RoadAt(ox, oy) == 0
                    && !(WorldBiomes.IsWater(_w.BiomeAt(ox, oy)) && _w.Places.Any(p => p.X == x && p.Y == y)))
                    return LandPixel(ox, oy, px, py, sx, sy, k);
            }
        }
        return LandPixel(x, y, px, py, sx, sy, k);
    }

    // A road-line pixel in a k×k cell. The one-pixel line runs through the "center" m (at k = 2, the top-left
    // pixel) to exits toward road neighbors on each side. A turn (exactly two neighbors at an angle) has no corner: a straight
    // segment between the exits, so a staircase of cells is drawn as a smooth diagonal line.
    private bool OnRoadLine(int x, int y, int sx, int sy, int k) =>
        OnThinLine(sx, sy, k, _w.RoadAt(x + 1, y) > 0, _w.RoadAt(x - 1, y) > 0, _w.RoadAt(x, y + 1) > 0, _w.RoadAt(x, y - 1) > 0);

    // A riverbed in a k×k cell: width follows river strength (level 1–9 grows downstream): a one-pixel line at the source,
    // wider toward the mouth, a large river fills the whole cell. A band through the center to river neighbors and to water (the mouth).
    private bool InRiverBed(int x, int y, int sx, int sy, int k, int level)
    {
        int w = k >= 4 ? Math.Clamp((level + 1) / 2, 1, 4) : level <= 3 ? 1 : 2;
        bool Conn(int nx, int ny) => _w.RiverAt(nx, ny) > 0 || (_w.InBounds(nx, ny) && WorldBiomes.IsWater(_w.BiomeAt(nx, ny)));
        bool r = Conn(x + 1, y), l = Conn(x - 1, y), d = Conn(x, y + 1), u = Conn(x, y - 1);
        if (w == 1) return OnThinLine(sx, sy, k, r, l, d, u);
        if (w >= k) return true;
        int a = (k - w) / 2, b = a + w;   // band [a, b)
        bool inX = sx >= a && sx < b, inY = sy >= a && sy < b;
        return (inX && inY)
            || (inY && ((sx >= b && r) || (sx < a && l)))
            || (inX && ((sy >= b && d) || (sy < a && u)));
    }

    // A one-pixel line through the cell's "center" to exits r/l/d/u; a turn is a straight segment with no corner.
    private static bool OnThinLine(int sx, int sy, int k, bool r, bool l, bool d, bool u)
    {
        int m = (k - 1) / 2;
        if ((r ^ l) && (d ^ u) && (r ? 1 : 0) + (l ? 1 : 0) + (d ? 1 : 0) + (u ? 1 : 0) == 2)
        {
            var (ax, ay) = r ? (k - 1, m) : (0, m);
            var (bx, by) = d ? (m, k - 1) : (m, 0);
            return OnSegment(ax, ay, bx, by, sx, sy);
        }
        return (sx == m && sy == m)
            || (sy == m && sx > m && r) || (sy == m && sx < m && l)
            || (sx == m && sy > m && d) || (sx == m && sy < m && u);
    }

    // Pixel (px, py) on the segment (ax, ay)–(bx, by), rasterized along the long axis.
    private static bool OnSegment(int ax, int ay, int bx, int by, int px, int py)
    {
        int n = Math.Max(Math.Abs(bx - ax), Math.Abs(by - ay));
        for (int i = 0; i <= n; i++)
        {
            int qx = n == 0 ? ax : (int)Math.Round(ax + (bx - ax) * i / (double)n);
            int qy = n == 0 ? ay : (int)Math.Round(ay + (by - ay) * i / (double)n);
            if (qx == px && qy == py) return true;
        }
        return false;
    }

    // ── Settlements up close (4×4 and 2×2 zooms) ──
    // Drawing radius in pixels (0 = icon only); from the wall to the center. A village is houses with no wall.
    public static int ArtRadius(string type, int k) => (type, k >= 4) switch
    {
        (WorldPlaceTypes.Capital, true) => 7,
        (WorldPlaceTypes.City, true) => 5,
        (WorldPlaceTypes.Town or WorldPlaceTypes.Port, true) => 4,
        (WorldPlaceTypes.Castle, true) => 3,
        (WorldPlaceTypes.Village, true) => 2,
        (WorldPlaceTypes.Capital, false) => 4,
        (WorldPlaceTypes.City, false) => 3,
        (WorldPlaceTypes.Town or WorldPlaceTypes.Port or WorldPlaceTypes.Castle, false) => 2,
        _ => 0,
    };

    private static readonly List<int> WallColor = [92, 86, 80];
    private static readonly List<int> StreetColor = [176, 162, 136];
    private static readonly List<int> RoofRed = [168, 78, 58];
    private static readonly List<int> RoofBrown = [128, 88, 60];
    private static readonly List<int> CourtColor = [150, 144, 134];

    private Dictionary<(int x, int y), int>? _settlementAt;

    private List<int>? TownPixel(int px, int py, int k)
    {
        _settlementAt ??= Enumerable.Range(0, _w.Places.Count).Where(i => ArtRadius(_w.Places[i].Type, 4) > 0)
            .GroupBy(i => (_w.Places[i].X, _w.Places[i].Y)).ToDictionary(g => g.Key, g => g.First());
        int x = FloorDiv(px, k), y = FloorDiv(py, k);
        for (int dx = -2; dx <= 2; dx++)
            for (int dy = -2; dy <= 2; dy++)
            {
                if (!_settlementAt.TryGetValue((x + dx, y + dy), out int i)) continue;
                var p = _w.Places[i];
                int R = ArtRadius(p.Type, k);
                if (R == 0 || (!_filter.Contains(Category(p.Type)) && i != _selected)) continue;
                int ox = px - (p.X * k + k / 2), oy = py - (p.Y * k + k / 2);
                double d = Math.Sqrt(ox * ox + oy * oy);
                if (d > R + 0.5 || WorldBiomes.IsWater(_w.BiomeAt(x, y))) continue;
                double h = Hash(px * 13 + 1, py * 29 + 3);
                if (p.Type == WorldPlaceTypes.Village)
                    return h < 0.4 ? (Hash(px, py) < 0.5 ? RoofRed : RoofBrown) : null;
                if (d >= R - 0.5) return ox == 0 || oy == 0 ? StreetColor : WallColor;   // wall; gates on all four sides
                if (p.Type == WorldPlaceTypes.Castle) return CourtColor;
                if (ox == 0 || oy == 0) return StreetColor;                                // streets from the gates to the center
                return h < 0.62 ? (Hash(px * 3, py * 5) < 0.6 ? RoofRed : RoofBrown) : StreetColor;
            }
        return null;
    }

    private static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);

    // A terrain pixel of cell (x, y) with texture: forest canopies, mountain slopes (lit from the northwest), hill ridges.
    private List<int> LandPixel(int x, int y, int px, int py, int sx, int sy, int k)
    {
        var col = TileColor(x, y);
        double h = Hash(px * 7 + 3, py * 13 + 5);
        return _w.BiomeAt(x, y) switch
        {
            WorldBiomes.Forest or WorldBiomes.DeepForest => h < 0.4 ? ColorHelper.Darker(col, 0.78) : h > 0.85 ? ColorHelper.Pale(col, 0.08) : col,
            // Taiga: dark spruces with snow between them.
            WorldBiomes.SnowForest => h < 0.45 ? ColorHelper.MixWith(col, [44, 74, 58], 0.7) : h > 0.75 ? ColorHelper.MixWith(col, [232, 238, 242], 0.75) : col,
            WorldBiomes.Mountains or WorldBiomes.Peaks => sx + sy < k - 1 ? ColorHelper.Pale(col, 0.18) : sx + sy >= k ? ColorHelper.Darker(col, 0.82) : col,
            WorldBiomes.Hills => sy < k / 2 && h < 0.5 ? ColorHelper.Pale(col, 0.08) : col,
            WorldBiomes.Swamp => h < 0.3 ? ColorHelper.MixWith(col, RiverColor, 0.35) : col,
            WorldBiomes.Grass or WorldBiomes.Plains => h < 0.15 ? ColorHelper.Darker(col, 0.9) : col,
            _ => col,
        };
    }

    private List<int> PixelColor(int tx, int ty, int s)
    {
        if (s == 1)
        {
            int roadLevel = _w.RoadAt(tx, ty);
            if (_w.RiverAt(tx, ty) >= 2) return roadLevel > 0 ? BridgeColor : RiverColor;
            if (roadLevel == 2) return RoadMain;
            if (roadLevel == 1) return ColorHelper.MixWith(TileColor(tx, ty), RoadPath, 0.6);
            return TileColor(tx, ty);
        }
        // An s×s block: a large river/highway stays visible when zoomed out; otherwise the dominant terrain.
        int riverMin = s switch { 2 => 4, 3 => 5, _ => 6 };
        Span<int> counts = stackalloc int[128];
        char best = WorldBiomes.Ocean;
        int bestN = -1;
        bool road = false;
        for (int dx = 0; dx < s; dx++)
            for (int dy = 0; dy < s; dy++)
            {
                int x = tx + dx, y = ty + dy;
                if (_w.RiverAt(x, y) >= riverMin) return RiverColor;
                if (s == 2 && _w.RoadAt(x, y) == 2) road = true;
                char b = _w.BiomeAt(x, y);
                int n = ++counts[b & 127];
                if (n > bestN) { bestN = n; best = b; }
            }
        if (road) return RoadMain;
        // Color of the central cell if it has the same terrain (with hillshading), otherwise the plain terrain color.
        int cx = tx + s / 2, cy = ty + s / 2;
        return _w.BiomeAt(cx, cy) == best ? TileColor(cx, cy) : WorldBiomes.Get(best).Color;
    }

    private static double Hash(int x, int y)
    {
        uint h = unchecked((uint)(x * 374761393 + y * 668265263));
        h = unchecked((h ^ (h >> 13)) * 1274126177);
        return ((h ^ (h >> 16)) & 0xFFFF) / 65535.0;
    }

    // ── Places: icons, visibility by zoom ──
    public static (char icon, List<int> color) Icon(string type) => type switch
    {
        WorldPlaceTypes.Capital => ('♦', [255, 214, 96]),
        WorldPlaceTypes.City => ('●', [244, 236, 214]),
        // Settlements by size: ● city, ○ town (a port is a light-blue ○), • village.
        WorldPlaceTypes.Town => ('○', [232, 226, 206]),
        WorldPlaceTypes.Port => ('○', [150, 200, 240]),
        WorldPlaceTypes.Village => ('•', [222, 214, 190]),
        WorldPlaceTypes.Castle => ('●', [236, 196, 52]),
        WorldPlaceTypes.Ruins => ('¤', [196, 150, 130]),
        WorldPlaceTypes.Dungeon => ('Ω', [220, 120, 110]),
        WorldPlaceTypes.Cave => ('∩', [196, 160, 120]),
        WorldPlaceTypes.Shrine => ('†', [200, 190, 240]),
        _ => ('?', [255, 255, 255]),
    };

    public static string PlaceImage(string type) => type switch
    {
        WorldPlaceTypes.Capital => "delapouite/castle",
        WorldPlaceTypes.City => "delapouite/tower-flag",
        WorldPlaceTypes.Town => "delapouite/family-house",
        WorldPlaceTypes.Port => "lorc/anchor",
        WorldPlaceTypes.Village => "delapouite/village",
        WorldPlaceTypes.Castle => "delapouite/watchtower",
        WorldPlaceTypes.Ruins => "delapouite/broken-wall",
        WorldPlaceTypes.Dungeon => "delapouite/crypt-entrance",
        WorldPlaceTypes.Cave => "delapouite/cave-entrance",
        _ => "delapouite/greek-temple",
    };

    public static string? BiomeImage(char biome) => biome switch
    {
        WorldBiomes.Ocean or WorldBiomes.Sea or WorldBiomes.Lake => "lorc/waves",
        WorldBiomes.Beach => "delapouite/sea-cliff",
        WorldBiomes.Plains => "delapouite/grass",
        WorldBiomes.Grass => "delapouite/high-grass",
        WorldBiomes.Forest or WorldBiomes.DeepForest => "delapouite/forest",
        WorldBiomes.SnowForest => "lorc/pine-tree",
        WorldBiomes.Hills => "delapouite/hills",
        WorldBiomes.Mountains or WorldBiomes.Peaks => "lorc/mountains",
        WorldBiomes.Swamp => "delapouite/swamp",
        WorldBiomes.Desert => "delapouite/desert",
        _ => "lorc/snowflake-1",
    };

    private static int Priority(string type) => type switch
    {
        WorldPlaceTypes.Capital => 0,
        WorldPlaceTypes.City => 1,
        WorldPlaceTypes.Castle => 2,
        WorldPlaceTypes.Town or WorldPlaceTypes.Port => 3,
        WorldPlaceTypes.Dungeon or WorldPlaceTypes.Ruins or WorldPlaceTypes.Cave or WorldPlaceTypes.Shrine => 4,
        _ => 5,
    };

    // Detail level counted from the farthest zoom: 0 = "world fits the window" (kingdoms and capitals),
    // 1 = + cities, fortresses, towns and large natural regions, 2 and closer = everything.
    private int Stage(int z) => MaxZoom - Math.Clamp(z, 0, MaxZoom);

    private static bool VisibleAt(string type, int st) => st switch
    {
        0 => type == WorldPlaceTypes.Capital,
        1 => Priority(type) <= 3,
        _ => true,
    };

    private static bool LabelAt(string type, int st) => st switch
    {
        0 => type == WorldPlaceTypes.Capital,
        1 => Priority(type) <= 1,
        2 => Priority(type) <= 3,
        _ => true,
    };

    private static List<int> FeatureColor(char biome) => biome switch
    {
        WorldBiomes.Lake => [150, 196, 236],
        WorldBiomes.Forest => [168, 210, 150],
        WorldBiomes.SnowForest => [206, 226, 222],
        WorldBiomes.Mountains => [214, 208, 200],
        WorldBiomes.Desert => [236, 214, 160],
        WorldBiomes.Swamp => [176, 196, 150],
        WorldGenerator.IslandCode => [226, 220, 196],
        _ => [210, 210, 196],
    };
}
