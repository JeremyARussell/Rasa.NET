﻿namespace Rasa.Timer;

public class Timer
{
    private readonly Dictionary<string, TimedItem> _timedItems = new();

    public void Add(string name, long timer, bool repeating, Action action)
    {
        lock (_timedItems)
        {
            _timedItems.Remove(name);

            _timedItems.Add(name, new TimedItem(name, timer, repeating, action));
        }
    }

    public void Remove(string name)
    {
        lock (_timedItems)
            _timedItems.Remove(name);
    }

    public void Update(long delta)
    {
        lock (_timedItems)
        {
            List<string>? toRemove = null;

            foreach (var item in _timedItems)
            {
                if (item.Value.Update(delta))
                {
                    item.Value.Action?.Invoke();

                    if (!item.Value.Repeating)
                    {
                        toRemove ??= new();

                        toRemove.Add(item.Key);
                    }
                }
            }

            if (toRemove == null)
                return;

            foreach (var key in toRemove)
                _timedItems.Remove(key);
        }
    }

    public void ResetTimer(string name)
    {
        lock (_timedItems)
        {
            if (_timedItems.TryGetValue(name, out var value))
                value.ResetTimer();
        }
    }
}
