using System.Drawing;

namespace Vorotex.K15.StatusLab;

internal enum PetTaskSurfaceState
{
    Collapsed,
    Stacked,
    Expanded
}

internal enum TaskPanelPlacementSide
{
    Above,
    Below
}

internal readonly record struct TaskPanelPlacement(
    TaskPanelPlacementSide Side,
    Rectangle Bounds);

internal readonly record struct TaskCardLayout(
    int TaskIndex,
    Rectangle Bounds,
    bool IsTopCard,
    bool ShowsText);

internal readonly record struct TaskSurfaceLayout(
    PetTaskSurfaceState State,
    Size PanelSize,
    IReadOnlyList<TaskCardLayout> Cards,
    int OverflowCount);

// Presentation-only policy. It does not read or change reducer/session state.
internal static class CodexPetTaskSurfacePolicy
{
    internal const PetTaskSurfaceState DefaultState = PetTaskSurfaceState.Stacked;
    internal const int MaximumExpandedRows = 5;
    internal const int PanelGap = 8;

    internal static PetTaskSurfaceState Cycle(PetTaskSurfaceState state) => state switch
    {
        PetTaskSurfaceState.Collapsed => PetTaskSurfaceState.Stacked,
        PetTaskSurfaceState.Stacked => PetTaskSurfaceState.Expanded,
        _ => PetTaskSurfaceState.Collapsed
    };

    internal static PetTaskSurfaceState Select(PetTaskSurfaceState state) =>
        Enum.IsDefined(state) ? state : DefaultState;

    internal static bool IsVisible(PetTaskSurfaceState state, int taskCount) =>
        taskCount > 0 && state != PetTaskSurfaceState.Collapsed;

    internal static int ExpandedVisibleRows(int total) =>
        Math.Min(MaximumExpandedRows, Math.Max(0, total));

    internal static int OverflowCount(int total) =>
        Math.Max(0, total - ExpandedVisibleRows(total));

    internal static int StackedHiddenLayerCount(int total) =>
        total <= 1 ? 0 : Math.Min(2, total - 1);

    internal static Size PanelSize(PetTaskSurfaceState state, int taskCount,
        Size cardSize = default)
    {
        cardSize = NormalizeCardSize(cardSize);
        if (!IsVisible(state, taskCount)) return Size.Empty;

        return state switch
        {
            PetTaskSurfaceState.Stacked => new(
                cardSize.Width + 24,
                18 + cardSize.Height + StackedHiddenLayerCount(taskCount) * 12),
            PetTaskSurfaceState.Expanded => new(
                cardSize.Width + 24,
                18 + ExpandedVisibleRows(taskCount) * (cardSize.Height + 8) +
                (OverflowCount(taskCount) > 0 ? 24 : 0)),
            _ => Size.Empty
        };
    }

    internal static TaskSurfaceLayout Layout(PetTaskSurfaceState state, int taskCount,
        Size cardSize = default)
    {
        cardSize = NormalizeCardSize(cardSize);
        var panelSize = PanelSize(state, taskCount, cardSize);
        if (!IsVisible(state, taskCount))
            return new(state, Size.Empty, Array.Empty<TaskCardLayout>(), 0);

        var cards = new List<TaskCardLayout>();
        if (state == PetTaskSurfaceState.Stacked)
        {
            var hidden = StackedHiddenLayerCount(taskCount);
            for (var layer = hidden; layer >= 1; layer--)
            {
                cards.Add(new(
                    layer,
                    new Rectangle(12 + layer * 7, 12 + layer * 12,
                        cardSize.Width, cardSize.Height),
                    false,
                    false));
            }
            cards.Add(new(0, new Rectangle(12, 12, cardSize.Width, cardSize.Height), true, true));
        }
        else
        {
            var rows = ExpandedVisibleRows(taskCount);
            for (var index = 0; index < rows; index++)
            {
                cards.Add(new(
                    index,
                    new Rectangle(12, 12 + index * (cardSize.Height + 8),
                        cardSize.Width, cardSize.Height),
                    index == 0,
                    true));
            }
        }

        return new(state, panelSize, cards, OverflowCount(taskCount));
    }

    private static Size NormalizeCardSize(Size cardSize) => new(
        cardSize.Width > 0 ? cardSize.Width : 296,
        cardSize.Height > 0 ? cardSize.Height : 54);
}

// Pure screen-aware placement seam. It uses only supplied rectangles and sizes.
internal static class TaskPanelPlacementPolicy
{
    internal static TaskPanelPlacement Place(
        Rectangle petBounds,
        Size desiredSize,
        Rectangle workingArea,
        TaskPanelPlacementSide preferredSide = TaskPanelPlacementSide.Below)
    {
        if (workingArea.Width <= 0 || workingArea.Height <= 0)
            return new(preferredSide, Rectangle.Empty);

        var size = new Size(
            Math.Clamp(desiredSize.Width, 0, workingArea.Width),
            Math.Clamp(desiredSize.Height, 0, workingArea.Height));
        var belowY = petBounds.Bottom + CodexPetTaskSurfacePolicy.PanelGap;
        var aboveY = petBounds.Top - CodexPetTaskSurfacePolicy.PanelGap - size.Height;
        var belowSpace = Math.Max(0, workingArea.Bottom - belowY);
        var aboveSpace = Math.Max(0, petBounds.Top - CodexPetTaskSurfacePolicy.PanelGap - workingArea.Top);
        var fitsBelow = size.Height <= belowSpace;
        var fitsAbove = size.Height <= aboveSpace;

        var side = preferredSide switch
        {
            TaskPanelPlacementSide.Above when fitsAbove => TaskPanelPlacementSide.Above,
            TaskPanelPlacementSide.Below when fitsBelow => TaskPanelPlacementSide.Below,
            _ when fitsBelow => TaskPanelPlacementSide.Below,
            _ when fitsAbove => TaskPanelPlacementSide.Above,
            _ when aboveSpace > belowSpace => TaskPanelPlacementSide.Above,
            _ => TaskPanelPlacementSide.Below
        };

        var desiredX = petBounds.Left + (petBounds.Width - size.Width) / 2;
        var desiredY = side == TaskPanelPlacementSide.Below ? belowY : aboveY;
        var maxX = Math.Max(workingArea.Left, workingArea.Right - size.Width);
        var maxY = Math.Max(workingArea.Top, workingArea.Bottom - size.Height);
        var location = new Point(
            Math.Clamp(desiredX, workingArea.Left, maxX),
            Math.Clamp(desiredY, workingArea.Top, maxY));
        return new(side, new Rectangle(location, size));
    }
}
