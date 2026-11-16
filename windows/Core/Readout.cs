using System;
using System.Collections.Generic;
using System.Linq;

namespace Uselim
{
    // The readout, the Mac's menu bar readout carried to Windows as it is: two lines beside the asterisk. Above, the
    // session's share and its time left; below, the weekly share, each model's share past half, and the time to the
    // weekly reset, in whole units. Each share takes its own tone.

    public sealed class Piece
    {
        public string Text;
        public Tone Tone;
    }

    public static class Readout
    {
        public static void Lines(IList<Limit> limits, DateTime now, out List<Piece> top, out List<Piece> bottom)
        {
            top = new List<Piece>();
            bottom = new List<Piece>();
            var session = limits.FirstOrDefault(l => l.Kind == "session");
            var weekly = limits.FirstOrDefault(l => l.Kind == "weekly_all");
            if (session != null)
            {
                Add(top, session.Percent + "%", Usage.ToneOf(session));
                if (session.Resets.HasValue) Add(top, Spans.Whole(session.Resets.Value, now), Tone.Normal);
            }
            if (weekly != null) Add(bottom, weekly.Percent + "%", Usage.ToneOf(weekly));
            // a model's own share joins past half; the panel keeps its row
            foreach (var m in limits.Where(l => l.Kind == "weekly_scoped" && l.Percent > 50)) Add(bottom, m.Percent + "%", Usage.ToneOf(m));
            if (weekly?.Resets != null) Add(bottom, Spans.Whole(weekly.Resets.Value, now), Tone.Normal);
        }

        private static void Add(List<Piece> line, string text, Tone tone)
        {
            if (line.Count > 0) line.Add(new Piece { Text = " · ", Tone = Tone.Normal });
            line.Add(new Piece { Text = text, Tone = tone });
        }

        public static string Plain(List<Piece> line) => string.Concat(line.Select(p => p.Text));
    }
}
