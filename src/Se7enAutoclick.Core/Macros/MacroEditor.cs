namespace Se7enAutoclick.Core.Macros;

/// <summary>
/// Retouches d'une macro sans la réenregistrer. Les « étapes » sont les gestes visibles dans la liste (touches,
/// clics, molette, attentes) ; les petits mouvements de souris suivent leur étape. Chaque opération rend une
/// nouvelle macro (l'ancienne reste intacte, pour annuler).
/// </summary>
public static class MacroEditor
{
    /// <summary>Indice dans <see cref="Macro.Events"/> de l'étape n° <paramref name="step"/>.</summary>
    public static int EventIndex(Macro m, int step)
    {
        int n = -1;
        for (int i = 0; i < m.Events.Count; i++)
            if (m.Events[i].IsStep && ++n == step) return i;
        throw new ArgumentOutOfRangeException(nameof(step));
    }

    public static int StepCount(Macro m) => m.Events.Count(e => e.IsStep);

    private static Macro With(Macro m, List<MacroEvent> events) =>
        new() { Name = m.Name, Created = m.Created, Events = events.OrderBy(e => e.T).ToList() };

    /// <summary>Supprime une étape. Un appui et son relâchement vont ensemble : supprimer l'un supprime l'autre.</summary>
    public static Macro DeleteStep(Macro m, int step)
    {
        var events = m.Events.ToList();
        int i = EventIndex(m, step);
        var e = events[i];
        int partner = e.Kind switch
        {
            EventKind.KeyDown => events.FindIndex(i + 1, x => x.Kind == EventKind.KeyUp && x.A == e.A),
            EventKind.ButtonDown => events.FindIndex(i + 1, x => x.Kind == EventKind.ButtonUp && x.A == e.A),
            EventKind.KeyUp => events.FindLastIndex(i, x => x.Kind == EventKind.KeyDown && x.A == e.A),
            EventKind.ButtonUp => events.FindLastIndex(i, x => x.Kind == EventKind.ButtonDown && x.A == e.A),
            _ => -1,
        };
        foreach (int k in new[] { i, partner }.Where(k => k >= 0).OrderDescending()) events.RemoveAt(k);
        return With(m, events);
    }

    /// <summary>Coupe tout ce qui précède l'étape : la macro commence par elle. Les touches tenues à cet instant restent tenues.</summary>
    public static Macro TrimBefore(Macro m, int step)
    {
        int i = EventIndex(m, step);
        double t0 = m.Events[i].T;
        var held = new Dictionary<int, MacroEvent>();
        var buttons = new Dictionary<int, MacroEvent>();
        foreach (var e in m.Events.Take(i))
        {
            if (e.Kind == EventKind.KeyDown) held[e.A] = e;
            else if (e.Kind == EventKind.KeyUp) held.Remove(e.A);
            else if (e.Kind == EventKind.ButtonDown) buttons[e.A] = e;
            else if (e.Kind == EventKind.ButtonUp) buttons.Remove(e.A);
        }
        var events = held.Values.Concat(buttons.Values).Select(e => e with { T = 0 }).ToList();
        events.AddRange(m.Events.Skip(i).Select(e => e with { T = e.T - t0 }));
        return With(m, events);
    }

    /// <summary>Coupe tout ce qui suit l'étape : la boucle repart juste après elle.</summary>
    public static Macro TrimAfter(Macro m, int step)
    {
        int i = EventIndex(m, step);
        var events = m.Events.Take(i + 1).ToList();
        // un petit temps après le dernier geste, pour que le jeu le prenne en compte avant de reboucler
        events.Add(new MacroEvent(m.Events[i].T + 50, EventKind.MoveBy, 0, 0));
        return With(m, events);
    }

    /// <summary>Délai (ms) entre l'étape précédente et celle-ci (depuis le début pour la première).</summary>
    public static double DelayBefore(Macro m, int step)
    {
        int i = EventIndex(m, step);
        double prev = step == 0 ? 0 : m.Events[EventIndex(m, step - 1)].T;
        return m.Events[i].T - prev;
    }

    /// <summary>Change le délai avant l'étape : elle et toute la suite sont avancées ou reculées d'autant.</summary>
    public static Macro SetDelayBefore(Macro m, int step, double ms)
    {
        int i = EventIndex(m, step);
        double shift = Math.Max(0, ms) - DelayBefore(m, step);
        double from = m.Events[i].T;
        var events = m.Events.Select((e, k) => k >= i ? e with { T = Math.Max(0, e.T + shift) } : e)
            // les mouvements de souris entre les deux étapes ne doivent pas dépasser la nouvelle place de l'étape
            .Select((e, k) => k < i && e.T > from + shift ? e with { T = Math.Max(0, from + shift) } : e)
            .ToList();
        return With(m, events);
    }

    /// <summary>Ajoute, juste avant l'étape, une attente : « jusqu'à ce que (x, y) prenne (ou perde) cette couleur ».</summary>
    public static Macro InsertWaitColor(Macro m, int beforeStep, int x, int y, int rgb, int tolerance, bool untilGone)
    {
        double t = beforeStep < StepCount(m) ? m.Events[EventIndex(m, beforeStep)].T : m.DurationMs;
        var events = m.Events.ToList();
        int at = beforeStep < StepCount(m) ? EventIndex(m, beforeStep) : events.Count;
        events.Insert(at, new MacroEvent(t, EventKind.WaitColor, x, y, rgb, untilGone, tolerance));
        // insertion stable : l'attente passe avant l'étape de même temps
        return new Macro { Name = m.Name, Created = m.Created, Events = events };
    }
}
