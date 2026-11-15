using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;

namespace DesktopSwitcher
{
    /// <summary>
    /// SVG path data into a GraphicsPath: M L H V C S Q T A Z, absolute and relative, with
    /// the shorthand SVG allows - repeated coordinates after a command, numbers run together
    /// ("1.5.5" is 1.5 then .5, "2-1" is 2 then -1), arc flags with nothing between them.
    /// Quadratics become cubics and arcs become cubic quarter-segments, since GraphicsPath
    /// has neither in the form SVG means them.
    /// </summary>
    static class SvgPath
    {
        public static void Append(string d, GraphicsPath path)
        {
            int i = 0;
            char cmd = ' ', prev = ' ';
            PointF cur = PointF.Empty, start = PointF.Empty, control = PointF.Empty;

            while (true)
            {
                SkipSeparators(d, ref i);
                if (i >= d.Length) break;

                if (IsCommand(d[i])) cmd = d[i++];
                else if (cmd == 'M') cmd = 'L';      // coordinates after a moveto are linetos
                else if (cmd == 'm') cmd = 'l';
                else if (cmd == ' ' || cmd == 'Z' || cmd == 'z') throw new FormatException("number without a command at " + i);

                bool relative = char.IsLower(cmd);
                float ox = relative ? cur.X : 0f, oy = relative ? cur.Y : 0f;

                switch (char.ToUpperInvariant(cmd))
                {
                    case 'M':
                        cur = start = new PointF(ox + Number(d, ref i), oy + Number(d, ref i));
                        path.StartFigure();
                        break;

                    case 'L':
                    {
                        var p = new PointF(ox + Number(d, ref i), oy + Number(d, ref i));
                        path.AddLine(cur, p);
                        cur = p;
                        break;
                    }

                    case 'H':
                    {
                        var p = new PointF(ox + Number(d, ref i), cur.Y);
                        path.AddLine(cur, p);
                        cur = p;
                        break;
                    }

                    case 'V':
                    {
                        var p = new PointF(cur.X, oy + Number(d, ref i));
                        path.AddLine(cur, p);
                        cur = p;
                        break;
                    }

                    case 'C':
                    {
                        var c1 = new PointF(ox + Number(d, ref i), oy + Number(d, ref i));
                        var c2 = new PointF(ox + Number(d, ref i), oy + Number(d, ref i));
                        var p = new PointF(ox + Number(d, ref i), oy + Number(d, ref i));
                        path.AddBezier(cur, c1, c2, p);
                        control = c2;
                        cur = p;
                        break;
                    }

                    case 'S':
                    {
                        // The first control point mirrors the last one, if the last segment
                        // was a cubic; otherwise it sits on the current point.
                        PointF c1 = IsCubic(prev) ? Mirror(control, cur) : cur;
                        var c2 = new PointF(ox + Number(d, ref i), oy + Number(d, ref i));
                        var p = new PointF(ox + Number(d, ref i), oy + Number(d, ref i));
                        path.AddBezier(cur, c1, c2, p);
                        control = c2;
                        cur = p;
                        break;
                    }

                    case 'Q':
                    {
                        var q = new PointF(ox + Number(d, ref i), oy + Number(d, ref i));
                        var p = new PointF(ox + Number(d, ref i), oy + Number(d, ref i));
                        AddQuadratic(path, cur, q, p);
                        control = q;
                        cur = p;
                        break;
                    }

                    case 'T':
                    {
                        PointF q = IsQuadratic(prev) ? Mirror(control, cur) : cur;
                        var p = new PointF(ox + Number(d, ref i), oy + Number(d, ref i));
                        AddQuadratic(path, cur, q, p);
                        control = q;
                        cur = p;
                        break;
                    }

                    case 'A':
                    {
                        float rx = Number(d, ref i), ry = Number(d, ref i), rotation = Number(d, ref i);
                        bool large = Flag(d, ref i), sweep = Flag(d, ref i);
                        var p = new PointF(ox + Number(d, ref i), oy + Number(d, ref i));
                        AddArc(path, cur, rx, ry, rotation, large, sweep, p);
                        cur = p;
                        break;
                    }

                    case 'Z':
                        path.CloseFigure();
                        cur = start;
                        break;

                    default:
                        throw new FormatException("unknown command " + cmd);
                }
                prev = cmd;
            }
        }

        static bool IsCommand(char c) { return "MmLlHhVvCcSsQqTtAaZz".IndexOf(c) >= 0; }
        static bool IsCubic(char c) { return "CcSs".IndexOf(c) >= 0; }
        static bool IsQuadratic(char c) { return "QqTt".IndexOf(c) >= 0; }

        static PointF Mirror(PointF control, PointF about)
        {
            return new PointF(2 * about.X - control.X, 2 * about.Y - control.Y);
        }

        static void SkipSeparators(string d, ref int i)
        {
            while (i < d.Length && (d[i] == ' ' || d[i] == ',' || d[i] == '\t' || d[i] == '\r' || d[i] == '\n')) i++;
        }

        static float Number(string d, ref int i)
        {
            SkipSeparators(d, ref i);
            int start = i;
            bool dot = false, exponent = false;
            if (i < d.Length && (d[i] == '-' || d[i] == '+')) i++;
            while (i < d.Length)
            {
                char c = d[i];
                if (c >= '0' && c <= '9') { i++; continue; }
                if (c == '.' && !dot && !exponent) { dot = true; i++; continue; }
                if ((c == 'e' || c == 'E') && !exponent)
                {
                    exponent = true;
                    i++;
                    if (i < d.Length && (d[i] == '-' || d[i] == '+')) i++;
                    continue;
                }
                break;
            }
            if (i == start) throw new FormatException("expected a number at " + start);
            return float.Parse(d.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        // An arc flag is one character, 0 or 1, and SVG lets the next value follow it with
        // no separator at all - "0 012-2" is large 0, sweep 1, then x = 2.
        static bool Flag(string d, ref int i)
        {
            SkipSeparators(d, ref i);
            if (i >= d.Length || (d[i] != '0' && d[i] != '1')) throw new FormatException("expected an arc flag at " + i);
            return d[i++] == '1';
        }

        static void AddQuadratic(GraphicsPath path, PointF a, PointF q, PointF b)
        {
            path.AddBezier(a,
                new PointF(a.X + 2f / 3f * (q.X - a.X), a.Y + 2f / 3f * (q.Y - a.Y)),
                new PointF(b.X + 2f / 3f * (q.X - b.X), b.Y + 2f / 3f * (q.Y - b.Y)),
                b);
        }

        /// <summary>
        /// An SVG endpoint arc as cubic Beziers, by the conversion in the SVG spec's
        /// implementation notes (F.6.5): find the centre and the start and sweep angles from
        /// the endpoints, radii and flags, then lay a cubic on each quarter or less.
        /// </summary>
        static void AddArc(GraphicsPath path, PointF p0, double rx, double ry, double rotationDeg,
                           bool large, bool sweep, PointF p1)
        {
            if (rx == 0 || ry == 0) { path.AddLine(p0, p1); return; }
            if (p0 == p1) return;
            rx = Math.Abs(rx);
            ry = Math.Abs(ry);

            double phi = rotationDeg * Math.PI / 180.0, cos = Math.Cos(phi), sin = Math.Sin(phi);
            double dx = (p0.X - p1.X) / 2.0, dy = (p0.Y - p1.Y) / 2.0;
            double x1 = cos * dx + sin * dy, y1 = -sin * dx + cos * dy;

            // Radii too small to span the endpoints are scaled up until they just do.
            double lambda = x1 * x1 / (rx * rx) + y1 * y1 / (ry * ry);
            if (lambda > 1) { rx *= Math.Sqrt(lambda); ry *= Math.Sqrt(lambda); }

            double num = rx * rx * ry * ry - rx * rx * y1 * y1 - ry * ry * x1 * x1;
            double den = rx * rx * y1 * y1 + ry * ry * x1 * x1;
            double co = Math.Sqrt(Math.Max(0, num / den)) * (large == sweep ? -1 : 1);
            double cxp = co * rx * y1 / ry, cyp = -co * ry * x1 / rx;
            double cx = cos * cxp - sin * cyp + (p0.X + p1.X) / 2.0;
            double cy = sin * cxp + cos * cyp + (p0.Y + p1.Y) / 2.0;

            double theta = Math.Atan2((y1 - cyp) / ry, (x1 - cxp) / rx);
            double delta = Math.Atan2((-y1 - cyp) / ry, (-x1 - cxp) / rx) - theta;
            if (sweep && delta < 0) delta += 2 * Math.PI;
            else if (!sweep && delta > 0) delta -= 2 * Math.PI;

            int segments = Math.Max(1, (int)Math.Ceiling(Math.Abs(delta) / (Math.PI / 2) - 1e-9));
            double step = delta / segments;
            double k = 4.0 / 3.0 * Math.Tan(step / 4);

            PointF from = p0;
            for (int s = 0; s < segments; s++)
            {
                double a = theta + s * step, b = a + step;
                double ca = Math.Cos(a), sa = Math.Sin(a), cb = Math.Cos(b), sb = Math.Sin(b);
                PointF c1 = OnEllipse(cx, cy, rx, ry, cos, sin, ca - k * sa, sa + k * ca);
                PointF c2 = OnEllipse(cx, cy, rx, ry, cos, sin, cb + k * sb, sb - k * cb);
                // The last segment ends exactly on the given endpoint, not on the rounded
                // trigonometry's idea of it, so the next command starts where it should.
                PointF to = s == segments - 1 ? p1 : OnEllipse(cx, cy, rx, ry, cos, sin, cb, sb);
                path.AddBezier(from, c1, c2, to);
                from = to;
            }
        }

        static PointF OnEllipse(double cx, double cy, double rx, double ry, double cos, double sin, double ex, double ey)
        {
            return new PointF((float)(cx + cos * rx * ex - sin * ry * ey), (float)(cy + sin * rx * ex + cos * ry * ey));
        }
    }
}
