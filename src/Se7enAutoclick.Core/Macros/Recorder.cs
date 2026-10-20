using Se7enAutoclick.Core.Input;

namespace Se7enAutoclick.Core.Macros;

/// <summary>
/// Reçoit les entrées du hub pendant l'enregistrement et en fait une macro propre : temps ramenés au début,
/// petits déplacements de souris regroupés (une souris à 1 000 Hz envoie 1 000 évènements par seconde),
/// et à la fin, touches encore enfoncées relâchées pour que la boucle reparte d'un état neutre.
/// </summary>
public sealed class Recorder : IInputSink
{
    /// <summary>Les déplacements plus rapprochés que ça sont additionnés (ms).</summary>
    public const double MoveMergeMs = 2.0;

    private readonly object _gate = new();
    private readonly List<MacroEvent> _events = [];
    private readonly double _start;
    private readonly HashSet<int> _keysDown = [];
    private readonly HashSet<MouseButton> _buttonsDown = [];

    public Recorder(double startMs) => _start = startMs;

    public int Count { get { lock (_gate) return _events.Count; } }

    private double Rel(double t) => Math.Max(0, t - _start);

    public void Key(double t, int vk, int scan, bool extended, bool down)
    {
        lock (_gate)
        {
            // la répétition automatique d'une touche tenue n'est pas un nouvel appui
            if (down && !_keysDown.Add(vk)) return;
            if (!down && !_keysDown.Remove(vk)) return;
            _events.Add(new MacroEvent(Rel(t), down ? EventKind.KeyDown : EventKind.KeyUp, vk, scan, 0, extended));
        }
    }

    public void Button(double t, MouseButton button, bool down, int x, int y)
    {
        lock (_gate)
        {
            if (down) _buttonsDown.Add(button);
            else if (!_buttonsDown.Remove(button)) return; // relâché d'un clic commencé avant l'enregistrement
            _events.Add(new MacroEvent(Rel(t), down ? EventKind.ButtonDown : EventKind.ButtonUp, (int)button, x, y));
        }
    }

    public void Wheel(double t, int delta, bool horizontal, int x, int y)
    {
        lock (_gate) _events.Add(new MacroEvent(Rel(t), horizontal ? EventKind.HWheel : EventKind.Wheel, delta, x, y));
    }

    public void MoveTo(double t, int x, int y) => AddMove(Rel(t), EventKind.MoveTo, x, y, merge: (a, b) => (b.A, b.B));

    public void MoveBy(double t, int dx, int dy) => AddMove(Rel(t), EventKind.MoveBy, dx, dy, merge: (a, b) => (a.A + b.A, a.B + b.B));

    private void AddMove(double t, EventKind kind, int a, int b, Func<MacroEvent, MacroEvent, (int, int)> merge)
    {
        lock (_gate)
        {
            var e = new MacroEvent(t, kind, a, b);
            // regroupe avec le dernier déplacement du même type s'il est tout proche (et rien entre les deux)
            for (int i = _events.Count - 1; i >= 0 && i >= _events.Count - 3; i--)
            {
                var last = _events[i];
                if (last.Kind is not (EventKind.MoveTo or EventKind.MoveBy)) break;
                if (last.Kind != kind) continue;
                if (t - last.T > MoveMergeMs) break;
                var (na, nb) = merge(last, e);
                _events[i] = last with { A = na, B = nb };
                return;
            }
            _events.Add(e);
        }
    }

    /// <summary>Termine l'enregistrement. <paramref name="stopMs"/> : moment de l'arrêt (temps du hub).
    /// <paramref name="stoppedByClick"/> : arrêté avec le bouton de l'appli ; le trajet de la souris jusqu'au
    /// bouton et ce clic ne font pas partie de la macro.</summary>
    public Macro Finish(string name, double stopMs, bool stoppedByClick = false)
    {
        lock (_gate)
        {
            double end = Rel(stopMs);
            if (stoppedByClick)
            {
                int click = _events.FindLastIndex(e => e.Kind == EventKind.ButtonDown && e.A == (int)MouseButton.Left);
                if (click >= 0)
                {
                    int cut = click;
                    while (cut > 0 && _events[cut - 1].Kind is EventKind.MoveTo or EventKind.MoveBy
                           && _events[click].T - _events[cut - 1].T < 2000)
                        cut--;
                    end = cut > 0 ? _events[cut - 1].T : 0;
                    _events.RemoveRange(cut, _events.Count - cut);
                    _buttonsDown.Remove(MouseButton.Left);
                }
            }
            foreach (var vk in _keysDown) _events.Add(new MacroEvent(end, EventKind.KeyUp, vk));
            foreach (var bt in _buttonsDown) _events.Add(new MacroEvent(end, EventKind.ButtonUp, (int)bt));
            _keysDown.Clear();
            _buttonsDown.Clear();
            var events = _events.OrderBy(e => e.T).ToList();
            // la boucle reprend à la fin de l'enregistrement (pas au dernier geste) : on garde la pause finale
            if (events.Count > 0 && end > events[^1].T) events.Add(new MacroEvent(end, EventKind.MoveBy, 0, 0));
            return new Macro { Name = name, Created = DateTime.Now, Events = events };
        }
    }
}
