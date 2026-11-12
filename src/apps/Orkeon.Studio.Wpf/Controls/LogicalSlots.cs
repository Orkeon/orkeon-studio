using System.Collections;
using System.Windows;

namespace Orkeon.Studio.Wpf.Controls;

/// <summary>
/// The extra content slots of the controls (<see cref="Alert.Actions"/>, <see cref="PropertyRow.Action"/>,
/// <see cref="EmptyState.Actions"/>, <see cref="StepTabs.SideContent"/>) are logical children of their
/// control, as a ContentControl's Content or a HeaderedContentControl's Header are. Without that, an
/// element set in a slot has no parent until the template presents it — never while the control is
/// collapsed —, so it inherits no DataContext and a RelativeSource FindAncestor binding inside it
/// fails (« Cannot find source for binding with reference RelativeSource FindAncestor »).
/// The control calls <see cref="Changed"/> from the slot's property callback, through its own
/// AddLogicalChild / RemoveLogicalChild, and lists the slots in its LogicalChildren with <see cref="Concat"/>.
/// </summary>
internal static class LogicalSlots
{
    /// <summary>Moves the logical child from the old value of a slot to the new one.</summary>
    public static void Changed(DependencyPropertyChangedEventArgs e, Action<object> remove, Action<object> add)
    {
        if (e.OldValue is not null)
        {
            remove(e.OldValue);
        }

        if (e.NewValue is not null)
        {
            add(e.NewValue);
        }
    }

    /// <summary>The base logical children, then the slots that hold something.</summary>
    public static IEnumerator Concat(IEnumerator? children, params object?[] slots)
    {
        var all = new List<object>();
        if (children is not null)
        {
            while (children.MoveNext())
            {
                all.Add(children.Current);
            }
        }

        all.AddRange(slots.OfType<object>());
        return all.GetEnumerator();
    }
}
