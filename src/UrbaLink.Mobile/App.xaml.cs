using Microsoft.Extensions.DependencyInjection;

namespace UrbaLink.Mobile;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
		// Screens use a light palette; native controls must use the same theme.
		UserAppTheme = AppTheme.Light;
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		return new Window(new AppShell());
	}
}
