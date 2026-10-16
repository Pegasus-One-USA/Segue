namespace FHIRBridge.Infrastructure.Cds.Handlers;

/// <summary>
/// SAMPLE clinical reference data for the built-in CDS handlers — a small, illustrative rule set so the feature
/// can be demonstrated and tested end to end. It is NOT a drug knowledge base: it is incomplete, unversioned and
/// has not been clinically validated. It REQUIRES CLINICAL REVIEW (pharmacist/physician sign-off) before any
/// clinical use; a production deployment should replace it with a licensed knowledge source.
/// </summary>
public static class CdsSampleReferenceData
{
    /// <summary>Appended to every card's detail so no reader mistakes a sample rule for vetted guidance.</summary>
    public const string CardNotice =
        "_SAMPLE rule shipped with Segue for demonstration. Requires clinical review before clinical use._";

    public const string RxNormSystem = "http://www.nlm.nih.gov/research/umls/rxnorm";
    public const string LoincSystem = "http://loinc.org";
    public const string SnomedSystem = "http://snomed.info/sct";
    public const string CvxSystem = "http://hl7.org/fhir/sid/cvx";
    public const string CptSystem = "http://www.ama-assn.org/go/cpt";

    /// <summary>A drug ingredient: lower-case name (matched as a whole word in order text), RxNorm ingredient
    /// code, and the drug classes it belongs to.</summary>
    public sealed record Ingredient(string Name, string RxNormCode, IReadOnlyList<string> Classes);

    /// <summary>A drug class an allergy can be recorded against (matched by name or SNOMED code).</summary>
    public sealed record DrugClass(
        string Key,
        string DisplayName,
        IReadOnlyList<string> Synonyms,
        IReadOnlyList<string> AllergyCodes);

    /// <summary>Cross-reactivity between two allergy classes (e.g. penicillins → cephalosporins).</summary>
    public sealed record CrossReactivity(string AllergyClass, string OrderClass, string Note);

    /// <summary>
    /// A drug–drug interaction. <see cref="A"/> and <see cref="B"/> are an ingredient name or "class:&lt;key&gt;".
    /// <see cref="Severity"/> is one of <see cref="InteractionSeverity"/>.
    /// </summary>
    public sealed record Interaction(string A, string B, string Severity, string Effect, string Advice);

    /// <summary>A renally cleared drug: review its dose below <see cref="ReviewBelow"/> eGFR, avoid below <see cref="AvoidBelow"/>.</summary>
    public sealed record RenalRule(string Ingredient, double ReviewBelow, double? AvoidBelow, string Advice);

    public static class InteractionSeverity
    {
        public const string Moderate = "moderate";
        public const string Major = "major";
        public const string Contraindicated = "contraindicated";

        public static int Rank(string severity) => severity switch
        {
            Contraindicated => 3,
            Major => 2,
            Moderate => 1,
            _ => 0,
        };
    }

    public static readonly IReadOnlyList<Ingredient> Ingredients =
    [
        new("amoxicillin", "723", ["penicillin"]),
        new("ampicillin", "733", ["penicillin"]),
        new("penicillin g", "7980", ["penicillin"]),
        new("penicillin v", "7984", ["penicillin"]),
        new("piperacillin", "8339", ["penicillin"]),
        new("dicloxacillin", "3356", ["penicillin"]),
        new("cephalexin", "2231", ["cephalosporin"]),
        new("cefazolin", "2180", ["cephalosporin"]),
        new("ceftriaxone", "2193", ["cephalosporin"]),
        new("cefuroxime", "2194", ["cephalosporin"]),
        new("sulfamethoxazole", "10180", ["sulfonamide-antibiotic"]),
        new("trimethoprim", "10829", []),
        new("ibuprofen", "5640", ["nsaid"]),
        new("naproxen", "7258", ["nsaid"]),
        new("ketorolac", "35827", ["nsaid"]),
        new("celecoxib", "140587", ["nsaid"]),
        new("aspirin", "1191", ["nsaid", "antiplatelet"]),
        new("clopidogrel", "32968", ["antiplatelet"]),
        new("codeine", "2670", ["opioid"]),
        new("morphine", "7052", ["opioid"]),
        new("tramadol", "10689", ["opioid", "serotonergic"]),
        new("warfarin", "11289", ["anticoagulant"]),
        new("apixaban", "1364430", ["anticoagulant"]),
        new("dabigatran", "1546356", ["anticoagulant"]),
        new("enoxaparin", "67108", ["anticoagulant"]),
        new("omeprazole", "7646", ["ppi"]),
        new("simvastatin", "36567", ["statin"]),
        new("atorvastatin", "83367", ["statin"]),
        new("clarithromycin", "21212", ["macrolide", "strong-cyp3a4-inhibitor"]),
        new("itraconazole", "28031", ["strong-cyp3a4-inhibitor"]),
        new("lisinopril", "29046", ["ace-inhibitor"]),
        new("enalapril", "3827", ["ace-inhibitor"]),
        new("spironolactone", "9997", ["potassium-sparing"]),
        new("potassium chloride", "8591", ["potassium"]),
        new("sildenafil", "136411", ["pde5-inhibitor"]),
        new("tadalafil", "358263", ["pde5-inhibitor"]),
        new("nitroglycerin", "4917", ["nitrate"]),
        new("isosorbide mononitrate", "6058", ["nitrate"]),
        new("methotrexate", "6851", []),
        new("sertraline", "36437", ["ssri", "serotonergic"]),
        new("fluoxetine", "4493", ["ssri", "serotonergic"]),
        new("linezolid", "190376", []),
        new("metformin", "6809", ["biguanide"]),
        new("nitrofurantoin", "7454", []),
        new("gabapentin", "25480", []),
        new("levofloxacin", "82122", ["fluoroquinolone"]),
        new("ciprofloxacin", "2551", ["fluoroquinolone"]),
    ];

    public static readonly IReadOnlyList<DrugClass> DrugClasses =
    [
        new("penicillin", "Penicillins", ["penicillin", "penicillins"], ["91936005", "294505008"]),
        new("cephalosporin", "Cephalosporins", ["cephalosporin", "cephalosporins"], []),
        new("sulfonamide-antibiotic", "Sulfonamide antibiotics", ["sulfonamide", "sulfonamides", "sulfa", "sulfa drugs"], []),
        new("nsaid", "NSAIDs", ["nsaid", "nsaids", "non-steroidal anti-inflammatory", "nonsteroidal anti-inflammatory"], []),
        new("opioid", "Opioids", ["opioid", "opioids", "opiate", "opiates"], []),
        new("fluoroquinolone", "Fluoroquinolones", ["fluoroquinolone", "fluoroquinolones", "quinolone"], []),
        new("macrolide", "Macrolides", ["macrolide", "macrolides"], []),
        new("statin", "Statins", ["statin", "statins"], []),
        new("ace-inhibitor", "ACE inhibitors", ["ace inhibitor", "ace inhibitors"], []),
    ];

    public static readonly IReadOnlyList<CrossReactivity> CrossReactivities =
    [
        new("penicillin", "cephalosporin",
            "Cross-reactivity between penicillins and cephalosporins is low (often quoted at 1–2%) but depends on the side chain and the reaction history."),
    ];

    public static readonly IReadOnlyList<Interaction> Interactions =
    [
        new("warfarin", "class:nsaid", InteractionSeverity.Major,
            "Increased bleeding risk (additive antiplatelet effect and GI mucosal injury).",
            "Avoid if possible; if required, use the lowest dose for the shortest time and monitor INR and for bleeding."),
        new("warfarin", "class:antiplatelet", InteractionSeverity.Major,
            "Increased bleeding risk.",
            "Confirm the combination is intended (e.g. specific cardiac indication) and monitor for bleeding."),
        new("warfarin", "class:fluoroquinolone", InteractionSeverity.Moderate,
            "May raise the INR.",
            "Monitor INR closely during and after the course."),
        new("simvastatin", "class:strong-cyp3a4-inhibitor", InteractionSeverity.Contraindicated,
            "Markedly increased simvastatin exposure; risk of myopathy and rhabdomyolysis.",
            "Do not combine. Hold simvastatin during the course or choose an alternative agent."),
        new("atorvastatin", "clarithromycin", InteractionSeverity.Moderate,
            "Increased atorvastatin exposure; risk of myopathy.",
            "Limit the atorvastatin dose or hold it during the course."),
        new("class:pde5-inhibitor", "class:nitrate", InteractionSeverity.Contraindicated,
            "Profound, potentially fatal hypotension.",
            "Do not combine."),
        new("class:ace-inhibitor", "spironolactone", InteractionSeverity.Moderate,
            "Risk of hyperkalemia.",
            "Check potassium and renal function before starting and periodically after."),
        new("class:ace-inhibitor", "potassium chloride", InteractionSeverity.Moderate,
            "Risk of hyperkalemia.",
            "Monitor serum potassium."),
        new("methotrexate", "trimethoprim", InteractionSeverity.Major,
            "Increased methotrexate toxicity (bone marrow suppression).",
            "Avoid the combination; choose an alternative antibiotic if possible."),
        new("clopidogrel", "omeprazole", InteractionSeverity.Moderate,
            "Omeprazole may reduce clopidogrel's antiplatelet effect.",
            "Consider an alternative acid suppressant such as pantoprazole."),
        new("class:ssri", "tramadol", InteractionSeverity.Major,
            "Risk of serotonin syndrome and lowered seizure threshold.",
            "Consider an alternative analgesic; if combined, monitor for serotonin toxicity."),
        new("class:serotonergic", "linezolid", InteractionSeverity.Major,
            "Risk of serotonin syndrome (linezolid is a weak MAO inhibitor).",
            "Avoid unless no alternative; monitor closely if combined."),
    ];

    public static readonly IReadOnlyList<RenalRule> RenalRules =
    [
        new("metformin", 45, 30,
            "Do not start when eGFR is 30–45; contraindicated below 30. Reassess the benefit if eGFR falls below 45."),
        new("nitrofurantoin", 30, 30,
            "Avoid when eGFR is below 30: reduced efficacy and increased toxicity."),
        new("gabapentin", 60, null,
            "Reduce the dose and/or lengthen the dosing interval according to renal function."),
        new("enoxaparin", 30, null,
            "Reduce the dose (e.g. once-daily dosing) when creatinine clearance is below 30 mL/min."),
        new("dabigatran", 50, 30,
            "Consider a dose reduction below 50; avoid below 30."),
        new("apixaban", 25, null,
            "Limited data below 25; review the indication and dose."),
        new("spironolactone", 45, 30,
            "Higher hyperkalemia risk; avoid below 30."),
        new("levofloxacin", 50, null,
            "Extend the dosing interval / reduce the dose below 50."),
        new("ciprofloxacin", 30, null,
            "Reduce the dose below 30."),
        new("tramadol", 30, null,
            "Extend the dosing interval and lower the maximum daily dose below 30."),
        new("sulfamethoxazole", 30, 15,
            "Halve the dose at 15–30; avoid below 15."),
    ];

    /// <summary>LOINC codes accepted as an eGFR result.</summary>
    public static readonly IReadOnlyList<string> EgfrLoincCodes = ["33914-3", "48642-3", "48643-1", "62238-1", "88293-6", "88294-4", "98979-8"];

    /// <summary>Codes (SNOMED, or ICD-10 prefixes) and terms that mark diabetes on a Condition.</summary>
    public static readonly IReadOnlyList<string> DiabetesSnomedCodes = ["44054006", "46635009", "73211009"];

    public static readonly IReadOnlyList<string> DiabetesIcd10Prefixes = ["E10", "E11", "E13"];

    public static readonly IReadOnlyList<string> DiabetesTerms = ["diabetes", "diabetic"];

    /// <summary>LOINC codes accepted as an HbA1c result.</summary>
    public static readonly IReadOnlyList<string> Hba1cLoincCodes = ["4548-4", "17856-6", "4549-2"];

    /// <summary>CVX codes for seasonal influenza vaccines.</summary>
    public static readonly IReadOnlyList<string> InfluenzaCvxCodes =
        ["88", "135", "140", "141", "150", "153", "155", "158", "161", "166", "168", "171", "185", "186", "197", "205"];

    public static readonly IReadOnlyList<string> InfluenzaTerms = ["influenza", "flu vaccine"];

    /// <summary>SNOMED codes and terms accepted as a colonoscopy.</summary>
    public static readonly IReadOnlyList<string> ColonoscopySnomedCodes = ["73761001", "310634005"];

    public static readonly IReadOnlyList<string> ColonoscopyTerms = ["colonoscopy"];

    /// <summary>Default codes the prior-auth handler flags (SAMPLE payer list).</summary>
    public static readonly IReadOnlyList<string> PriorAuthDefaultCodes =
    [
        CptSystem + "|70553",
        CptSystem + "|72148",
        CptSystem + "|27447",
        RxNormSystem + "|327361",
        "E0601",
    ];
}
