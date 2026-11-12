# Persistent Jobs Rework – Testversion 0.01

Eigener Ersatz für PersistentJobs (Banjobeni). Diese erste Version ist eine **Test-/Messversion**:
Sie soll beweisen, dass „nie löschen + einfrieren“ die Performance rettet, bevor die Job-Logik
(Phase 2) darauf aufgebaut wird.

## Was v0.01 schon macht

| Funktion | Verhalten |
|---|---|
| **Wagen nie löschen** | Der Unused-Car-Deleter des Spiels löscht nichts mehr. Jede trotzdem passierende Löschung wird gezählt und mit Aufrufer ins Log geschrieben (`[Delete] …`). |
| **Bahnhof spawnt nur einmal** | Der **Vanilla-Generator** läuft beim ersten Besuch ganz normal (inkl. beladener Züge zum Entladen, Beladejobs enden im Ausfahrgleis). Danach spawnt der Bahnhof nie wieder neue Wagen. Merker werden im Spielstand gespeichert. |
| **Jobs verfallen nicht** | Angebotene Jobs bleiben bestehen, wenn man den Bahnhof verlässt. |
| **Einfrieren** | Stehende Garnituren ohne Spieler in der Nähe (Host + alle MP-Clients) werden eingefroren: Schlafzustand des Spiels + alle Rigidbodies kinematisch. Die Wagen **bleiben in der Welt**, Gleisbelegung bleibt für andere Mods unverändert. |
| **Kein Desync** | Zielgleise werden nach der Job-Erstellung nie mehr verändert (es gibt keinen „Ersatzgleis“-Mechanismus wie bei PJ). |
| **Debug** | FPS-Overlay, `perf_log.csv`, Weltspawn aller Bahnhöfe, „alles einfrieren“-A/B-Schalter, Komponenten-Statistik. |

## Noch NICHT enthalten (Phase 2)

- Neue Jobs für Wagen, deren Jobkette fertig ist. In v0.01 bleiben solche Wagen einfach stehen,
  d. h. nach dem Abarbeiten der ersten Jobs gibt es an einem Bahnhof keine neuen mehr.
  → **Nur in einer neuen Test-Karriere benutzen.**
- Einstellungs-Sync Host → Clients (in v0.01 nicht nötig, alles läuft nur auf dem Host).
- Übersetzungen (UI vorerst Englisch).

## Bauen

1. Pfade in `PersistentJobsRework/PersistentJobsRework.csproj` prüfen (`DvDir`, `MpApiDll`).
   `MultiplayerAPI.dll` wird nur zum Kompilieren gebraucht – ohne Multiplayer läuft die Mod trotzdem.
2. Build in Visual Studio / `dotnet build -c Release`.
   Debug-Build kopiert DLL + info.json direkt nach `…\Derail Valley\Mods\PersistentJobsRework\`,
   Release-Build ruft zusätzlich `package.ps1` auf (ZIP nach `dist\`, Hotfix wird automatisch hochgezählt,
   info.json immer UTF-8 ohne BOM).
3. **Banjobenis PersistentJobsMod deaktivieren** – laufen beide, bleibt diese Mod absichtlich inaktiv.

## Testablauf (Performance)

1. In den Mod-Einstellungen: *Show performance overlay* und *Write perf_log.csv* an,
   *On NEW career: generate jobs at ALL stations* an.
2. Neue Karriere starten. Nach dem Laden + 5 s generieren alle Bahnhöfe nacheinander ihre Jobs.
   Erwartung: FPS fallen deutlich (alles wach).
3. Danach läuft der Countdown (Standard 20 s), dann wird eingefroren.
   Erwartung: FPS erholen sich, Overlay zeigt viele „frozen“.
4. A/B im Bahnhof: *Freeze ALL standing trains, ignore distance* an/aus schalten und die FPS vergleichen.
5. Bringt Stufe 1 zu wenig: *Log component statistics* drücken, dann testweise *EXPERIMENTAL level 2*.

Bitte zurückschicken: `perf_log.csv` und den Ausschnitt aus `Player.log` mit `[PersistentJobsRework]`
(vor allem Zeilen mit `Patch skipped`, `World spawn` und `[Delete]`).
