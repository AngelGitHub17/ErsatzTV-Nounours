using System.Globalization;

namespace ErsatzTV.Nounours.Audience;

public static class AudienceFormat
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    /// <summary>Minutes-spectateurs → « 3 h 20 » / « 45 min ».</summary>
    public static string Hours(double minutes)
    {
        int total = (int)Math.Round(minutes);
        if (total < 60)
        {
            return $"{total} min";
        }

        return total % 60 == 0 ? $"{total / 60} h" : $"{total / 60} h {total % 60:00}";
    }

    public static string Number(double value, string format = "0.#") => value.ToString(format, Fr);
}
