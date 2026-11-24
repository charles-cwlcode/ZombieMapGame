using System;
using System.Collections.Generic;
using ZombieMap.Core.Buildings;
using ZombieMap.Core.Geo;

namespace ZombieMap.Core.Zombies
{
    /// <summary>
    /// A walkability grid over a patch of the map (square cells of a few metres, in metres east/north of
    /// an origin): building footprints are blocked, everything else is open ground. Zombies find their way
    /// around buildings with A* over it, then the path is straightened wherever there's a clear line.
    /// </summary>
    public sealed class ObstacleGrid
    {
        public readonly LatLng Origin;
        public readonly double CellMetres;
        public readonly int Width, Height;
        private readonly double _minX, _minY;
        private readonly bool[] _blocked;

        /// <param name="halfSizeMetres">The grid covers this far east, west, north and south of the origin.</param>
        public ObstacleGrid(LatLng origin, double halfSizeMetres, double cellMetres = 2.5)
        {
            Origin = origin;
            CellMetres = cellMetres;
            _minX = -halfSizeMetres;
            _minY = -halfSizeMetres;
            Width = Height = (int)Math.Ceiling(2 * halfSizeMetres / cellMetres);
            _blocked = new bool[Width * Height];
        }

        public int BlockedCount
        {
            get
            {
                int n = 0;
                foreach (bool b in _blocked) if (b) n++;
                return n;
            }
        }

        /// <summary>Marks every grid cell whose middle is inside the polygon (a building outline) as blocked.</summary>
        public void AddPolygon(IReadOnlyList<LatLng> ring)
        {
            if (ring == null || ring.Count < 3) return;
            var pts = new Vec2[ring.Count];
            double minY = double.MaxValue, maxY = double.MinValue;
            for (int i = 0; i < ring.Count; i++)
            {
                pts[i] = GeoMath.ToLocal(Origin, ring[i]);
                minY = Math.Min(minY, pts[i].Y);
                maxY = Math.Max(maxY, pts[i].Y);
            }
            int row0 = Math.Max(0, (int)Math.Floor((minY - _minY) / CellMetres));
            int row1 = Math.Min(Height - 1, (int)Math.Ceiling((maxY - _minY) / CellMetres));
            var xs = new List<double>();
            for (int row = row0; row <= row1; row++)
            {
                double y = _minY + (row + 0.5) * CellMetres;
                xs.Clear();
                for (int i = 0, j = pts.Length - 1; i < pts.Length; j = i++)
                {
                    var a = pts[i];
                    var b = pts[j];
                    if ((a.Y > y) != (b.Y > y)) xs.Add(a.X + (y - a.Y) / (b.Y - a.Y) * (b.X - a.X));
                }
                xs.Sort();
                for (int k = 0; k + 1 < xs.Count; k += 2)
                {
                    int c0 = Math.Max(0, (int)Math.Ceiling((xs[k] - _minX) / CellMetres - 0.5));
                    int c1 = Math.Min(Width - 1, (int)Math.Floor((xs[k + 1] - _minX) / CellMetres - 0.5));
                    for (int c = c0; c <= c1; c++) _blocked[row * Width + c] = true;
                }
            }
        }

        // ---------- conversions ----------

        public bool InBounds(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;

        public bool ToCell(LatLng p, out int x, out int y)
        {
            var v = GeoMath.ToLocal(Origin, p);
            x = (int)Math.Floor((v.X - _minX) / CellMetres);
            y = (int)Math.Floor((v.Y - _minY) / CellMetres);
            return InBounds(x, y);
        }

        public LatLng ToLatLng(int x, int y) =>
            GeoMath.Offset(Origin, _minX + (x + 0.5) * CellMetres, _minY + (y + 0.5) * CellMetres);

        public bool IsBlocked(int x, int y) => !InBounds(x, y) || _blocked[y * Width + x];

        /// <summary>Outside the grid counts as open: the grid only knows about buildings it was given.</summary>
        public bool IsFree(LatLng p) => !ToCell(p, out int x, out int y) || !_blocked[y * Width + x];

        /// <summary>True if the straight line from a to b doesn't pass through a building.</summary>
        public bool LineClear(LatLng a, LatLng b)
        {
            double d = GeoMath.Distance(a, b);
            int steps = Math.Max(1, (int)Math.Ceiling(d / (CellMetres * 0.5)));
            var va = GeoMath.ToLocal(Origin, a);
            var vb = GeoMath.ToLocal(Origin, b);
            for (int i = 0; i <= steps; i++)
            {
                double t = (double)i / steps;
                double x = va.X + (vb.X - va.X) * t, y = va.Y + (vb.Y - va.Y) * t;
                int cx = (int)Math.Floor((x - _minX) / CellMetres), cy = (int)Math.Floor((y - _minY) / CellMetres);
                if (InBounds(cx, cy) && _blocked[cy * Width + cx]) return false;
            }
            return true;
        }

        /// <summary>The nearest open spot to p (p itself if it's open), searching out to maxMetres.</summary>
        public bool NearestFree(LatLng p, double maxMetres, out LatLng free)
        {
            free = p;
            if (!ToCell(p, out int x0, out int y0)) return true;
            if (!_blocked[y0 * Width + x0]) return true;
            int maxR = (int)Math.Ceiling(maxMetres / CellMetres);
            for (int r = 1; r <= maxR; r++)
                for (int dy = -r; dy <= r; dy++)
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
                        int x = x0 + dx, y = y0 + dy;
                        if (InBounds(x, y) && !_blocked[y * Width + x])
                        {
                            free = ToLatLng(x, y);
                            return true;
                        }
                    }
            return false;
        }

        // ---------- A* ----------

        private static readonly int[] DX = { 1, -1, 0, 0, 1, 1, -1, -1 };
        private static readonly int[] DY = { 0, 0, 1, -1, 1, -1, 1, -1 };

        /// <summary>
        /// A walking route from one point to another around buildings (null if there's none, or it would
        /// leave the grid). Ends on open ground nearest the target if the target itself is inside a building.
        /// </summary>
        // Search buffers, reused between searches (a grid belongs to one grain, so one search at a time).
        // A stamp per search marks which entries are current, so nothing has to be cleared.
        private float[] _g;
        private int[] _parent;
        private int[] _seen;
        private int[] _closed;
        private int _stamp;

        /// <summary>How far round an obstacle a route may go, beyond the box around start and end.</summary>
        public const double DetourMetres = 80;

        public List<LatLng> FindPath(LatLng from, LatLng to, int maxExpanded = 60000)
        {
            if (!NearestFree(from, 15, out var start) || !NearestFree(to, 25, out var goal)) return null;
            if (LineClear(start, goal)) return new List<LatLng> { from, goal };
            if (!ToCell(start, out int sx, out int sy) || !ToCell(goal, out int gx, out int gy)) return null;

            int n = Width * Height;
            if (_g == null)
            {
                _g = new float[n];
                _parent = new int[n];
                _seen = new int[n];
                _closed = new int[n];
            }
            int stamp = ++_stamp;
            // Only search a window round the start and end: routes don't need the whole grid.
            int margin = (int)Math.Ceiling(DetourMetres / CellMetres);
            int wx0 = Math.Max(0, Math.Min(sx, gx) - margin), wx1 = Math.Min(Width - 1, Math.Max(sx, gx) + margin);
            int wy0 = Math.Max(0, Math.Min(sy, gy) - margin), wy1 = Math.Min(Height - 1, Math.Max(sy, gy) + margin);
            bool Open(int x, int y) => x >= wx0 && x <= wx1 && y >= wy0 && y <= wy1 && !_blocked[y * Width + x];

            var open = new MinHeap();
            int s = sy * Width + sx, goalIndex = gy * Width + gx;
            _g[s] = 0;
            _parent[s] = -1;
            _seen[s] = stamp;
            open.Push(s, Heuristic(sx, sy, gx, gy));
            int expanded = 0;
            bool found = false;
            while (open.Count > 0)
            {
                int cur = open.Pop();
                if (_closed[cur] == stamp) continue;
                if (cur == goalIndex) { found = true; break; }
                _closed[cur] = stamp;
                if (++expanded > maxExpanded) return null;
                int cx = cur % Width, cy = cur / Width;
                for (int k = 0; k < 8; k++)
                {
                    int nx = cx + DX[k], ny = cy + DY[k];
                    if (!Open(nx, ny)) continue;
                    if (k >= 4 && (!Open(cx + DX[k], cy) || !Open(cx, cy + DY[k]))) continue; // no cutting corners
                    int ni = ny * Width + nx;
                    if (_closed[ni] == stamp) continue;
                    float cost = _g[cur] + (k >= 4 ? 1.41421356f : 1f);
                    if (_seen[ni] == stamp && cost >= _g[ni]) continue;
                    _seen[ni] = stamp;
                    _g[ni] = cost;
                    _parent[ni] = cur;
                    open.Push(ni, cost + Heuristic(nx, ny, gx, gy));
                }
            }
            if (!found) return null;
            var parent = _parent;

            var cells = new List<LatLng>();
            for (int i = goalIndex; i >= 0; i = parent[i]) cells.Add(ToLatLng(i % Width, i / Width));
            cells.Reverse();
            cells[0] = from;
            cells[cells.Count - 1] = goal;
            return Smooth(cells);
        }

        /// <summary>Drops corners the zombie can see past, so it walks straight where it can.</summary>
        private List<LatLng> Smooth(List<LatLng> path)
        {
            var result = new List<LatLng> { path[0] };
            int i = 0, last = path.Count - 1;
            while (i < last)
            {
                int j = i + 1;
                while (j < last && LineClear(path[i], path[j + 1])) j++;
                result.Add(path[j]);
                i = j;
            }
            return result;
        }

        private static float Heuristic(int x, int y, int gx, int gy)
        {
            int dx = Math.Abs(x - gx), dy = Math.Abs(y - gy);
            return Math.Max(dx, dy) + 0.41421356f * Math.Min(dx, dy);
        }

        /// <summary>Binary heap of (index, priority), smallest first. Duplicates are skipped by the closed set.</summary>
        private sealed class MinHeap
        {
            private readonly List<(int item, float priority)> _items = new List<(int, float)>();
            public int Count => _items.Count;

            public void Push(int item, float priority)
            {
                _items.Add((item, priority));
                int i = _items.Count - 1;
                while (i > 0)
                {
                    int p = (i - 1) / 2;
                    if (_items[p].priority <= _items[i].priority) break;
                    (_items[p], _items[i]) = (_items[i], _items[p]);
                    i = p;
                }
            }

            public int Pop()
            {
                int top = _items[0].item;
                _items[0] = _items[_items.Count - 1];
                _items.RemoveAt(_items.Count - 1);
                int i = 0;
                while (true)
                {
                    int l = 2 * i + 1, r = l + 1, m = i;
                    if (l < _items.Count && _items[l].priority < _items[m].priority) m = l;
                    if (r < _items.Count && _items[r].priority < _items[m].priority) m = r;
                    if (m == i) break;
                    (_items[m], _items[i]) = (_items[i], _items[m]);
                    i = m;
                }
                return top;
            }
        }
    }
}
