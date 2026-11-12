using System.Collections;
using System.Collections.Generic;
using System.Linq;
using DV.Logic.Job;

namespace PersistentJobsRework
{
    /// <summary>
    /// Volle Eingangsgleise (I) blockieren sonst ganze Bahnhoefe: Der Generator findet am Zielbahnhof kein
    /// I-Gleis mit genug Platz und verwirft den Job (z. B. IMW -> SM, FRS -> SW, laut Diagnose nur 1-23 m frei).
    /// Waehrend der Erzeugung bietet diese Klasse deshalb zusaetzlich O-Gleise desselben Bahnhofs an (S nie - FH/LH enden nie auf S).
    /// Das Zielgleis steht damit schon bei der Erzeugung fest (Booklet = Host = Clients, kein Desync).
    /// L, P, T und sonstige Gleise werden nie angeboten.
    /// </summary>
    internal static class InboundFallback
    {
        private static int _used;
        public static int Used => _used;

        private static bool On => Main.IsAuthority && Main.Settings.UseInboundFallback && Capacity.IsGenerating;

        private static double Required(object[] args)
        {
            if (args == null) return -1;
            foreach (var a in args)
            {
                if (a is float f) return f;
                if (a is double d) return d;
            }
            return -1;
        }

        /// <summary>Yard, wenn ALLE Kandidaten Eingangsgleise genau eines Bahnhofs sind, sonst null.</summary>
        private static Yard InboundYard(IEnumerable<Track> candidates)
        {
            var list = candidates?.Where(t => t != null).ToList();
            if (list == null || list.Count == 0) return null;
            var station = StationController.GetStationByYardID(list[0].ID.yardId);
            var yard = station?.logicStation?.yard;
            if (yard?.TransferInTracks == null) return null;
            // Eigener (gerade generierender) Bahnhof = hier werden Wagen GESPAWNT (z. B. beladene Zuege zum Entladen).
            // Dafuer nie ausweichen: ein Spawn auf ein knappes O-Gleis kann Wagen ineinander setzen -> Schaeden.
            if (Capacity.IsGeneratingYard(list[0].ID.yardId)) return null;
            return list.All(t => yard.TransferInTracks.Contains(t)) ? yard : null;
        }

        private static List<Track> Alternatives(Yard yard, double required)
        {
            var yto = YardTracksOrganizer.Instance;
            var result = new List<Track>();
            if (yto == null) return result;
            foreach (var group in new[] { yard.TransferOutTracks }) // FH/LH nie auf S
            {
                if (group == null) continue;
                // bester Treffer zuerst: das Gleis, auf das der Zug gerade noch passt
                // bereits in dieser Erzeugung angebotene Meter abziehen - sonst bekommt ein Gleis mehrfach denselben Platz
                result.AddRange(group.Where(t => t != null && Free(yto, t) >= required)
                                     .OrderBy(t => Free(yto, t)));
            }
            return result;
        }

        /// <summary>Aus Capacity.Filter_Postfix: leere Liste fuer I-Gleise -> S/O-Gleise mit genug Platz ergaenzen.</summary>
        public static void ExtendFilter(object[] args, object result)
        {
            if (!On || !(result is IList list) || list.Count > 0) return;
            var candidates = args?.OfType<IEnumerable<Track>>().FirstOrDefault();
            double required = Required(args);
            if (required <= 0) return;
            var yard = InboundYard(candidates);
            if (yard == null) return;

            var alt = Alternatives(yard, required);
            if (alt.Count == 0) return;
            foreach (var t in alt) list.Add(t);
            Note(alt[0], required);
        }

        /// <summary>Aus Capacity.PickTrack_Postfix: kein I-Gleis gefunden -> passendes S/O-Gleis liefern.</summary>
        public static Track Pick(object[] args, Track result)
        {
            if (result != null || !On) return result;
            var candidates = args?.OfType<IEnumerable<Track>>().FirstOrDefault();
            double required = Required(args);
            if (required <= 0) return null;
            var yard = InboundYard(candidates);
            if (yard == null) return null;

            var alt = Alternatives(yard, required).FirstOrDefault();
            if (alt != null) Note(alt, required);
            return alt;
        }

        private static readonly Dictionary<Track, double> Offered = new Dictionary<Track, double>();
        private static int _offeredFrame = -1;

        private static double Free(YardTracksOrganizer yto, Track t)
        {
            if (!Capacity.IsGenerating && Offered.Count > 0) Offered.Clear();
            return yto.GetFreeSpaceOnTrack(t) - (Offered.TryGetValue(t, out double o) ? o : 0);
        }

        private static void Note(Track t, double required)
        {
            // Filter und Pick derselben Anfrage im selben Frame nur einmal zaehlen
            if (_offeredFrame != UnityEngine.Time.frameCount || !Offered.ContainsKey(t))
                Offered[t] = (Offered.TryGetValue(t, out double o) ? o : 0) + required;
            _offeredFrame = UnityEngine.Time.frameCount;
            _used++;
            if (_used <= 20)
                Main.Log($"Inbound tracks of {t.ID.yardId} full - offering {t.ID.FullDisplayID} for {required:0} m instead.");
        }
    }
}
