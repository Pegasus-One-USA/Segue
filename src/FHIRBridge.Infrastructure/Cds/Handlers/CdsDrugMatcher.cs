using System.Text.Json.Nodes;
using FHIRBridge.Application.DTOs.Cds;
using static FHIRBridge.Infrastructure.Cds.Handlers.CdsSampleReferenceData;

namespace FHIRBridge.Infrastructure.Cds.Handlers;

/// <summary>Maps FHIR medication/allergy concepts onto the SAMPLE ingredient and class tables.</summary>
public static class CdsDrugMatcher
{
    private static readonly Dictionary<string, Ingredient> ByRxNormCode =
        Ingredients.ToDictionary(ingredient => ingredient.RxNormCode, StringComparer.Ordinal);

    private static readonly Dictionary<string, DrugClass> ClassesByKey =
        DrugClasses.ToDictionary(drugClass => drugClass.Key, StringComparer.Ordinal);

    /// <summary>
    /// Ingredients named in a concept: an RxNorm ingredient code (system RxNorm, its OID form, or none), or the
    /// ingredient's name as a whole word in any display/text — which also covers product codes such as "amoxicillin
    /// 500 MG Oral Capsule" and Epic's upper-case medicationReference displays ("DEXTROMETHORPHAN HBR 15 MG/5ML PO SYRP").
    /// </summary>
    public static IReadOnlyList<Ingredient> IngredientsOf(JsonNode? concept)
    {
        var found = new List<Ingredient>();
        foreach (var coding in CdsFhirReader.Codings(concept))
        {
            if (coding.Code is not null
                && (coding.System is null
                    || string.Equals(coding.System, RxNormSystem, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(coding.System, CdsFixCodeSystems.RxNormOid, StringComparison.OrdinalIgnoreCase))
                && ByRxNormCode.TryGetValue(coding.Code, out var byCode))
            {
                found.Add(byCode);
            }
        }

        var text = CdsFhirReader.SearchText(concept);
        if (text.Length > 0)
        {
            found.AddRange(Ingredients.Where(ingredient => CdsFhirReader.ContainsTerm(text, ingredient.Name)));
        }

        return found.Distinct().ToArray();
    }

    /// <summary>Drug classes an allergy concept names, directly (e.g. "Penicillins", SNOMED 91936005) or via an ingredient.</summary>
    public static IReadOnlySet<string> ClassesOfAllergen(JsonNode? concept, IReadOnlyList<Ingredient> ingredients)
    {
        var classes = ingredients.SelectMany(ingredient => ingredient.Classes).ToHashSet(StringComparer.Ordinal);
        var text = CdsFhirReader.SearchText(concept);
        foreach (var drugClass in DrugClasses)
        {
            if (drugClass.Synonyms.Any(synonym => CdsFhirReader.ContainsTerm(text, synonym))
                || (drugClass.AllergyCodes.Count > 0 && CdsFhirReader.HasCode(concept, drugClass.AllergyCodes)))
            {
                classes.Add(drugClass.Key);
            }
        }

        return classes;
    }

    public static string ClassName(string classKey) =>
        ClassesByKey.TryGetValue(classKey, out var drugClass) ? drugClass.DisplayName : classKey;

    /// <summary>True when <paramref name="ingredient"/> is the rule side <paramref name="term"/> (a name or "class:&lt;key&gt;").</summary>
    public static bool Matches(Ingredient ingredient, string term) =>
        term.StartsWith("class:", StringComparison.Ordinal)
            ? ingredient.Classes.Contains(term["class:".Length..], StringComparer.Ordinal)
            : string.Equals(ingredient.Name, term, StringComparison.Ordinal);
}
