namespace Phantom.Services;

internal static class TweakScriptNormalizer
{
    public static string WrapDetectScript(string detectScript)
    {
        var body = (detectScript ?? string.Empty).Trim();
        return "$___phantomDetect='Not Applied'\n" +
               "try {\n" +
               "  $___phantomRaw = @(\n" +
               body + "\n" +
               "  ) | Out-String\n" +
               "  $___phantomRaw = $___phantomRaw.Trim()\n" +
               "  if(-not [string]::IsNullOrWhiteSpace($___phantomRaw)) { $___phantomDetect = $___phantomRaw }\n" +
               "}\n" +
               "catch {\n" +
               "  $___phantomDetect='Not Applied'\n" +
               "}\n" +
               "$___phantomDetect";
    }

    public static string WrapMutationScript(string script)
    {
        var body = (script ?? string.Empty).Trim();
        return "$ErrorActionPreference='Continue'\n" +
               "$PSDefaultParameterValues['*:ErrorAction']='Stop'\n" +
               "Set-StrictMode -Version Latest\n" +
               body;
    }
}
