namespace GuardianX.Core.Power;

public enum PlugAction { None, TurnOff, TurnOn, ChargerNotOnPlug }

/// <summary>
/// Keeps the battery between <c>resume</c> and <c>limit</c> by switching the smart plug the charger is on.
/// Pure logic, so every rule is unit tested.
/// </summary>
public static class ChargeController
{
    public static PlugAction Decide(bool onAc, double percent, int limit, int resume, bool? plugOn, bool chargeToFull)
    {
        if (plugOn is null) return PlugAction.None;          // plug unreachable: do nothing rather than guess
        if (chargeToFull) return plugOn == false ? PlugAction.TurnOn : PlugAction.None;
        if (plugOn == true && onAc && percent >= limit) return PlugAction.TurnOff;
        if (plugOn == false && onAc) return PlugAction.ChargerNotOnPlug; // plug is off but power still arrives
        if (plugOn == false && !onAc && percent <= resume) return PlugAction.TurnOn;
        return PlugAction.None;
    }
}
