using System;
using System.Collections.Generic;
using System.Linq;

namespace WindowsDynamicBar.Core.Buddy;

public sealed record OutfitItem(
    BuddyOutfit Outfit,
    string Key,
    string DisplayName,
    string Description,
    CosmeticPackId Pack,
    bool IsFree
);

public sealed record CosmeticPackInfo(
    CosmeticPackId Id,
    string Title,
    string Description,
    IReadOnlyList<BuddyOutfit> Outfits,
    string Badge
);

public static class BuddyCosmeticCatalog
{
    public static readonly IReadOnlyList<OutfitItem> Outfits = new List<OutfitItem>
    {
        new(BuddyOutfit.None, "none", "Casual", "Original sunny yellow buddy with green antenna.", CosmeticPackId.Core, true),
        new(BuddyOutfit.Cap, "cap", "Red Cap", "Sporty red cap with a brim extending to the right.", CosmeticPackId.Core, true),
        new(BuddyOutfit.Bow, "bow", "Pink Bow", "Cute pink ribbons on the upper ear.", CosmeticPackId.Core, true),
        new(BuddyOutfit.Scarf, "scarf", "Warm Scarf", "Cozy red knit scarf with a playful tail.", CosmeticPackId.Core, true),
        new(BuddyOutfit.Crown, "crown", "Gold Crown", "Royal four-pointed crown for dynamic royalty.", CosmeticPackId.Core, true),
        new(BuddyOutfit.Glasses, "glasses", "Round Glasses", "Gentle lavender spectacles.", CosmeticPackId.Core, true),
        new(BuddyOutfit.Sunglasses, "sunglasses", "Cool Shades", "Stylish dark shades for bright afternoons.", CosmeticPackId.Core, true),
        new(BuddyOutfit.WizardHat, "wizard", "Wizard Hat", "Star-tipped mystical sorcerer hat.", CosmeticPackId.Fantasy, false),
        new(BuddyOutfit.NeonVisor, "neon", "Cyber Visor", "High-tech cyan neon visor straight from 2099.", CosmeticPackId.Cyber, false),
        new(BuddyOutfit.WinterBeanie, "beanie", "Winter Beanie", "Toasty knitted beanie with a snowy white pom-pom.", CosmeticPackId.Seasonal, false),
        new(BuddyOutfit.PartyHat, "party", "Party Hat", "Festive polka-dotted cone hat for special celebrations.", CosmeticPackId.Seasonal, false),
    };

    public static readonly IReadOnlyList<CosmeticPackInfo> Packs = new List<CosmeticPackInfo>
    {
        new(CosmeticPackId.Core, "Starter Pack", "Standard outfits and classic animations included free.", new[] { BuddyOutfit.None, BuddyOutfit.Cap, BuddyOutfit.Bow, BuddyOutfit.Scarf, BuddyOutfit.Crown, BuddyOutfit.Glasses, BuddyOutfit.Sunglasses }, "FREE"),
        new(CosmeticPackId.Fantasy, "Fantasy Pack", "Wizard hats, mystical accessories, and enchanted sparkle trails.", new[] { BuddyOutfit.WizardHat }, "DEMO PACK"),
        new(CosmeticPackId.Cyber, "Cyber Pack", "Neon visors, holographic glints, and futuristic cyber vibes.", new[] { BuddyOutfit.NeonVisor }, "DEMO PACK"),
        new(CosmeticPackId.Seasonal, "Seasonal Pack", "Winter beanies, party celebration hats, and festive moods.", new[] { BuddyOutfit.WinterBeanie, BuddyOutfit.PartyHat }, "DEMO PACK"),
        new(CosmeticPackId.Animation, "Animation Pack", "Extra dance routines, dramatic sleeping poses, and sparkle chases.", Array.Empty<BuddyOutfit>(), "DEMO PACK")
    };

    public static OutfitItem GetOutfit(BuddyOutfit outfit) =>
        Outfits.FirstOrDefault(o => o.Outfit == outfit) ?? Outfits[0];

    public static bool IsOutfitUnlocked(BuddyOutfit outfit, HashSet<string>? ownedPacks, bool demoMode)
    {
        var item = GetOutfit(outfit);
        if (item.IsFree) return true;
        if (demoMode) return true;
        if (ownedPacks is null) return false;
        return ownedPacks.Contains(item.Pack.ToString());
    }
}
