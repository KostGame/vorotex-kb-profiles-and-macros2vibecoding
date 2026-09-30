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

internal readonly record struct TaskSurfaceHostContract(
    Color TransparencyKey,
    bool PaintsBackground);

// Presentation-only policy. It does not read or change reducer/session state.
internal static class CodexPetTaskSurfacePolicy
{
    internal const PetTaskSurfaceState DefaultState = PetTaskSurfaceState.Stacked;
    internal const int DefaultCardWidth = 296;
    internal const int DefaultCardHeight = 80;
    internal const int MaximumExpandedRows = 5;
    internal const int ExpandedRowGap = 8;
    internal const int StackedLayerOffsetX = 7;
    internal const int StackedLayerOffsetY = 12;
    internal const int CardCornerRadius = 10;
    internal const int OverflowGap = 4;
    internal const int OverflowHeight = 18;
    internal static readonly TaskSurfaceHostContract HostContract = new(
        Color.FromArgb(255, 255, 0, 255), false);

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
                cardSize.Width + StackedHiddenLayerCount(taskCount) * StackedLayerOffsetX,
                cardSize.Height + StackedHiddenLayerCount(taskCount) * StackedLayerOffsetY),
            PetTaskSurfaceState.Expanded => new(
                cardSize.Width,
                ExpandedContentHeight(taskCount, cardSize) +
                (OverflowCount(taskCount) > 0 ? OverflowGap + OverflowHeight : 0)),
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
                    new Rectangle(layer * StackedLayerOffsetX, layer * StackedLayerOffsetY,
                        cardSize.Width, cardSize.Height),
                    false,
                    false));
            }
            cards.Add(new(0, new Rectangle(0, 0, cardSize.Width, cardSize.Height), true, true));
        }
        else
        {
            var rows = ExpandedVisibleRows(taskCount);
            for (var index = 0; index < rows; index++)
            {
                cards.Add(new(
                    index,
                    new Rectangle(0, index * (cardSize.Height + ExpandedRowGap),
                        cardSize.Width, cardSize.Height),
                    index == 0,
                    true));
            }
        }

        return new(state, panelSize, cards, OverflowCount(taskCount));
    }

    internal static Rectangle OverflowBounds(TaskSurfaceLayout layout, Size cardSize = default)
    {
        cardSize = NormalizeCardSize(cardSize);
        if (layout.State != PetTaskSurfaceState.Expanded || layout.OverflowCount <= 0)
            return Rectangle.Empty;

        var rows = ExpandedVisibleRows(layout.Cards.Count + layout.OverflowCount);
        var top = ExpandedContentHeight(rows, cardSize) + OverflowGap;
        return new(4, top, Math.Max(1, layout.PanelSize.Width - 8), OverflowHeight);
    }

    private static int ExpandedContentHeight(int total, Size cardSize) =>
        ExpandedVisibleRows(total) * cardSize.Height +
        Math.Max(0, ExpandedVisibleRows(total) - 1) * ExpandedRowGap;

    private static Size NormalizeCardSize(Size cardSize) => new(
        cardSize.Width > 0 ? cardSize.Width : DefaultCardWidth,
        cardSize.Height > 0 ? cardSize.Height : DefaultCardHeight);

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
        var belowY = petBounds.Bottom + CodexPetTaskSurfacePolicy.ExpandedRowGap;
        var aboveY = petBounds.Top - CodexPetTaskSurfacePolicy.ExpandedRowGap - size.Height;
        var belowSpace = Math.Max(0, workingArea.Bottom - belowY);
        var aboveSpace = Math.Max(0, petBounds.Top - CodexPetTaskSurfacePolicy.ExpandedRowGap - workingArea.Top);
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
