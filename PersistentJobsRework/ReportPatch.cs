using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace PersistentJobsRework
{
    /// <summary>
    /// Auftragsbericht (Vergütungsseite): Abzug fuers Ausweichgleis als eigene Zeile - wie in LongerLoadingDelay.
    /// Die Zeile "Strafe für Zeitüberziehung" (vom Spiel sonst fest mit "/" gefuellt) bekommt:
    ///   Beschriftung "Abzug Ersatz für G3O (-5 %)" + Betrag "-$205" (rot, das faerbt das Spiel selbst)
    ///   Basis / Zeitbonus ungekuerzt, Gesamt unveraendert (= ausgezahlter Betrag).
    /// Ohne Abzug bleibt der Bericht exakt wie im Original. Alles per Reflection (keine TMPro-/Booklet-Referenz noetig).
    /// </summary>
    internal static class ReportPatch
    {
        /// <summary>Daten-Objekt der Seite -> neue Beschriftung (wird in FillInData eingesetzt).</summary>
        private static readonly Dictionary<object, string> Labels = new Dictionary<object, string>();

        private static CultureInfo _cc;
        private static string Money(float v)
        {
            if (_cc == null)
            {
                try
                {
                    var t = Patches.FindGameTypePublic("LocalizationAPI");
                    _cc = t?.GetProperty("CC", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.GetValue(null, null) as CultureInfo
                       ?? t?.GetField("CC", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.GetValue(null) as CultureInfo;
                }
                catch { }
                if (_cc == null) _cc = CultureInfo.InvariantCulture;
            }
            return v.ToString("N0", _cc);
        }

        private static string BuildLabel(AlternativeTracks.CompletedDeduction d)
        {
            string pct = d.Percent.ToString("0.#");
            return d.Assigned.Count == 1
                ? Loc.F("rep_alt", d.Assigned[0], pct)
                : Loc.F("rep_alt_multi", d.Assigned.Count, pct);
        }

        /// <summary>Postfix auf BookletCreator_JobReport.GetReportTemplateData(Job_data ...).</summary>
        public static void TemplateData_Postfix(object[] __args, object __result)
        {
            try
            {
                if (!Main.IsActive || __args == null || !(__result is IList pages)) return;
                object data = null;
                foreach (var a in __args) if (a != null && a.GetType().Name == "Job_data") { data = a; break; }
                if (data == null) return;

                var d = AlternativeTracks.FindCompleted(Reflect.Get<string>(data, "ID"));
                if (d == null || d.Percent <= 0.001f) return;
                float factor = 1f - d.Percent / 100f;
                if (factor <= 0.0001f) return;

                float basePay = Reflect.Get<float>(data, "basePayment");
                float bonusPay = Reflect.Get<float>(data, "bonusPayment");
                float total = Mathf.Round(Reflect.Get<float>(data, "totalPayment"));

                // Ist nur das Gesamt gekuerzt (GetWageForTheJob), stimmen Basis/Bonus schon.
                // Sind Basis/Bonus mitgekuerzt, auf ungekuerzt zurueckrechnen.
                float origBase = Mathf.Round(basePay), origBonus = Mathf.Round(bonusPay);
                if (origBase + origBonus - total < 1f)
                {
                    origBase = Mathf.Round(basePay / factor);
                    origBonus = Mathf.Round(bonusPay / factor);
                }
                float deduction = Mathf.Max(0f, origBase + origBonus - total);
                if (deduction < 1f) return;

                foreach (var page in pages)
                {
                    if (page == null || page.GetType().Name != "JobReportPaymentTemplatePaperData") continue;
                    if (Reflect.Get<string>(page, "expirationPenalty") != "/") continue;   // echte Strafe vorhanden -> nicht ueberschreiben
                    Reflect.Set(page, "basePayment", "+$" + Money(origBase));
                    Reflect.Set(page, "bonusPayment", "+$" + Money(origBonus));
                    Reflect.Set(page, "expirationPenalty", "-$" + Money(deduction));
                    Labels[page] = BuildLabel(d);
                    Main.Log($"Job report {Reflect.Get<string>(data, "ID")}: base {origBase} bonus {origBonus} deduction {deduction} (-{d.Percent:0.#}%).");
                }
            }
            catch (Exception ex) { Main.WarningOnce("report-data", "Job report patch failed: " + ex.Message); }
        }

        /// <summary>Postfix auf JobReportPaymentTemplatePaper.FillInData: Beschriftung der Abzugszeile tauschen.</summary>
        public static void FillInData_Postfix(object __instance)
        {
            try
            {
                var data = Reflect.Get<object>(__instance, "data");
                if (data == null || !Labels.TryGetValue(data, out string label)) return;
                Labels.Remove(data);

                var value = Reflect.Get<Component>(__instance, "expirationPenalty");
                if (value == null || !(__instance is Component paper)) return;
                var skip = new HashSet<Component> { value };
                foreach (var n in new[] { "basePayment", "bonusPayment", "totalPayment", "totalPaymentText" })
                {
                    var c = Reflect.Get<Component>(__instance, n);
                    if (c != null) skip.Add(c);
                }

                // Beschriftung = das TextMeshPro-Feld auf derselben Hoehe wie der Betrag, das kein Datenfeld ist
                Component best = null;
                float bestDist = float.MaxValue, y = value.transform.position.y;
                foreach (var c in paper.GetComponentsInChildren<Component>(true))
                {
                    if (c == null || skip.Contains(c) || c.GetType().Name != "TextMeshProUGUI") continue;
                    float dist = Mathf.Abs(c.transform.position.y - y);
                    if (dist < bestDist) { bestDist = dist; best = c; }
                }
                if (best == null) return;

                // Lokalisierungs-Komponente wuerde den Text sonst zuruecksetzen
                foreach (var mb in best.GetComponents<MonoBehaviour>())
                    if (mb != null && mb.GetType().Name == "Localize") mb.enabled = false;

                float size = Reflect.Get<float>(best, "fontSize");
                Reflect.Set(best, "enableAutoSizing", true);
                if (size > 0f)
                {
                    Reflect.Set(best, "fontSizeMax", size);
                    Reflect.Set(best, "fontSizeMin", size * 0.6f);
                }
                Reflect.Set(best, "enableWordWrapping", false);
                Reflect.Set(best, "text", label);
            }
            catch (Exception ex) { Main.WarningOnce("report-label", "Job report label failed: " + ex.Message); }
        }
    }
}
