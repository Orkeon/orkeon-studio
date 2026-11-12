using System.Collections;
using System.Windows;

namespace Orkeon.Studio.Wpf.Controls;

/// <summary>
/// The extra content slots of the controls (<see cref="Alert.Actions"/>, <see cref="PropertyRow.Action"/>,
/// <see cref="EmptyState.Actions"/>, <see cref="StepTabs.SideContent"/>) are logical children of their
/// control, as a ContentControl's Content or a HeaderedContentControl's Header are. Without that, an
/// element set in a slot has no parent until the template presents it, never while the control is
/// collapsed: it inherits no DataContext and a RelativeSource FindAncestor binding inside it fails
/// ("Cannot find source for binding with reference RelativeSource FindAncestor").
/// The control calls <see cref="Changed"/> from the slot's property callback, through its own
/// AddLogicalChild / RemoveLogicalChild, and lists the slots in its LogicalChildren with <see cref="Concat"/>.
/// HarmonisationComponentConformityTests holds every object-typed property of Controls/ to this.
/// </summary>
internal static class LogicalSlots
{
    /// <summary>
    /// Moves the logical child from the old value of a slot to the new one. An element that already
    /// has another logical parent is left to it, as ContentControl does: a template that forwards a
    /// slot its outer control owns (a TemplateBinding) only presents the element, and adopting it
    /// would throw "Specified element is already the logical child of another element".
    /// </summary>
    public static void Changed(DependencyObject owner, DependencyPropertyChangedEventArgs e, Action<object> remove, Action<object> add)
    {
        if (e.OldValue is not null && (e.OldValue is not DependencyObject old || LogicalTreeHelper.GetParent(old) == owner))
        {
            remove(e.OldValue);
        }

        if (e.NewValue is not null && (e.NewValue is not DependencyObject value || LogicalTreeHelper.GetParent(value) is null))
        {
            add(e.NewValue);
        }
    }

    /// <summary>The base logical children, then the slots that hold something.</summary>
    public static IEnumerator Concat(IEnumerator? children, params object?[] slots) => Enumerate(children, slots).GetEnumerator();

    private static IEnumerable<object> Enumerate(IEnumerator? children, object?[] slots)
    {
        if (children is not null)
        {
            while (children.MoveNext())
            {
                yield return children.Current;
            }
        }

        foreach (var slot in slots)
        {
            if (slot is not null)
            {
                yield return slot;
            }
        }
    }
}
