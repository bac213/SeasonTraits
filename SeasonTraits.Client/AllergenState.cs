using System.Collections.Generic;
using UnityEngine;

namespace SeasonTraits.Client;

/// <summary>
/// Client-side cache of the profile's pre-rolled allergen template ids for the allergic trait.
/// The three allergens are rolled locally (no server round-trip) from a curated food/drink/med pool,
/// so the trait works entirely on the client.
/// </summary>
public static class AllergenState
{
    // Curated pool of food / drink / non-medkit medication template ids.
    private static readonly string[] AllergenPool =
    {
        // food
        "57347d3d245977448f7b7f61", // borodinskiye
        "57347d7224597744596b4e72", // canned_beef
        "57347da92459774491567cf5", // beefstew
        "57347d9c245977448b40fa85", // herring
        "5673de654bdc2d180f8b456d", // saury can
        "57347d692459774491567cf1", // peas
        "5734773724597737fd047c14", // condensed_milk
        "590c5f0d86f77413997acfab", // mre
        "544fb6cc4bdc2d34748b456e", // slippers
        "57347d8724597744596b4e76", // squashspread
        // drinks
        "57513f07245977207e26a311", // apple juice
        "57513f9324597720a7128161", // grand juice
        "57513fcc24597720a31c09a6", // vita juice
        "544fb62a4bdc2dfb738b4568", // juice_army
        "575146b724597720a27126d5", // milk
        "57514643245977207f2c2d09", // tarcola
        "5c0fa877d174af02a012e1cf", // aquamari
        "5751496424597720a27126da", // hotrod
        // meds (non-medkit)
        "544fb25a4bdc2dfb738b4567", // bandage
        "544fb37f4bdc2dee738b4567", // painkiller
        "544fb3f34bdc2d03748b456a", // morphine
        "5751a89d24597722aa0e8db0", // golden star balm
        "590c695186f7741e566b64a2", // augmentin
        "5af0548586f7743a532b7e99", // ibuprofen
        "544fb3364bdc2d34748b456a", // splint
        "5c0e530286f7747fa1419862", // propital
    };

    private static readonly HashSet<string> Allergens = new();

    public static void EnsureAllergens(bool hasAllergic)
    {
        if (!hasAllergic)
        {
            Allergens.Clear();
            return;
        }
        if (Allergens.Count == 0)
        {
            RollAllergens();
        }
    }

    public static bool IsAllergen(string templateId)
        => templateId != null && Allergens.Contains(templateId);

    private static void RollAllergens()
    {
        Allergens.Clear();
        var picked = new HashSet<int>();
        while (Allergens.Count < 3 && picked.Count < AllergenPool.Length)
        {
            var idx = Random.Range(0, AllergenPool.Length);
            if (picked.Add(idx))
            {
                Allergens.Add(AllergenPool[idx]);
            }
        }
    }
}
