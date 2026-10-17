namespace UrbaLink.Mobile;

internal static class InputBehavior
{
    public static Keyboard SentenceKeyboard { get; } = Keyboard.Create(
        KeyboardFlags.CapitalizeSentence | KeyboardFlags.Spellcheck | KeyboardFlags.Suggestions);

    public static void Configure()
    {
#if ANDROID
        Microsoft.Maui.Handlers.EntryHandler.Mapper.AppendToMapping("AccountAutofill", (handler, view) =>
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return;
            var hint = (view as Entry)?.AutomationId switch
            {
                "account-email" => "username",
                "account-password" => "password",
                "account-new-password" => "newPassword",
                _ => null
            };
            handler.PlatformView.ImportantForAutofill = hint is null
                ? Android.Views.ImportantForAutofill.No
                : Android.Views.ImportantForAutofill.Yes;
            if (hint is not null) handler.PlatformView.SetAutofillHints(hint);
        });
        Microsoft.Maui.Handlers.EditorHandler.Mapper.AppendToMapping("NoContentAutofill", (handler, _) =>
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(26))
                handler.PlatformView.ImportantForAutofill = Android.Views.ImportantForAutofill.No;
        });
#endif
    }

    // MAUI reuses one Activity: finish autofill before replacing the login form.
    public static void CompleteLogin() => FinishAutofill(true);
    public static void CancelLogin() => FinishAutofill(false);

    private static void FinishAutofill(bool commit)
    {
#if ANDROID
        if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return;
        using var serviceClass = Java.Lang.Class.FromType(typeof(Android.Views.Autofill.AutofillManager));
        var manager = Platform.CurrentActivity?.GetSystemService(serviceClass)
            as Android.Views.Autofill.AutofillManager;
        if (commit) manager?.Commit();
        else manager?.Cancel();
#endif
    }
}
