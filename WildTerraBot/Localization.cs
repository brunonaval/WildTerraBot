using System.Globalization;
using System.Threading;

namespace WildTerraBot
{
    internal static class Localization
    {
        public static void SetCulture(string cultureName)
        {
            if (string.IsNullOrWhiteSpace(cultureName))
                return;

            CultureInfo culture;
            try
            {
                culture = CultureInfo.GetCultureInfo(cultureName);
            }
            catch (CultureNotFoundException)
            {
                return;
            }

            Thread.CurrentThread.CurrentCulture = culture;
            Thread.CurrentThread.CurrentUICulture = culture;
            CultureInfo.DefaultThreadCurrentCulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            Properties.Resources.Culture = culture;
        }
    }
}
