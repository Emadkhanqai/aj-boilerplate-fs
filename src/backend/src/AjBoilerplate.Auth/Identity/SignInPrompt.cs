namespace AjBoilerplate.Auth.Identity;

public enum SignInPrompt
{
    None = 0,

    Login,

    SelectAccount,
}

public static class SignInPrompts
{
    public static SignInPrompt Parse(string? requested) => requested switch
    {
        "login" => SignInPrompt.Login,
        "select_account" => SignInPrompt.SelectAccount,
        _ => SignInPrompt.None,
    };

    public static string? ToParameterValue(this SignInPrompt prompt) => prompt switch
    {
        SignInPrompt.Login => "login",
        SignInPrompt.SelectAccount => "select_account",
        _ => null,
    };
}
