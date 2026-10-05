namespace UrbaLink.Mobile;

internal static class CardActions
{
    public static Button Menu(string description)
    {
        var button = new Button
        {
            Text = "⋮", FontSize = 26, Padding = 0,
            WidthRequest = 48, HeightRequest = 48,
            MinimumWidthRequest = 48, MinimumHeightRequest = 48,
            BackgroundColor = Colors.Transparent, TextColor = Color.FromArgb("#52665F"),
            BorderWidth = 0, CornerRadius = 24,
            HorizontalOptions = LayoutOptions.End, VerticalOptions = LayoutOptions.Center
        };
        SemanticProperties.SetDescription(button, description);
        SemanticProperties.SetHint(button, "Abre el menú de acciones");
        return button;
    }
}
