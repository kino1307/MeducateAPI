namespace Meducate.Infrastructure.LLM;

using System.Text.Json;
using System.Text.RegularExpressions;
using Meducate.Application.Helpers;
using Meducate.Domain.Entities;
using Meducate.Domain.Services;
using Microsoft.SemanticKernel;

internal sealed partial class SemanticKernelLLMProcessor(Kernel kernel, ILLMProcessorLogger? logger = null) : ILLMProcessor
{
    private readonly Kernel _kernel = kernel;
    private readonly ILLMProcessorLogger? _logger = logger;

    private const int MaxRawTextSize = 10 * 1024 * 1024;
    private const int MaxTopicNameLength = 200;
    private const int ClassifyBatchSize = 50;
    private const int CategoryBatchSize = 50;
    private const int MaxSnippetLength = 150;

    static SemanticKernelLLMProcessor()
    {
        if (ClassifyBatchSize <= 0)
            throw new InvalidOperationException($"{nameof(ClassifyBatchSize)} must be greater than 0.");
        if (CategoryBatchSize <= 0)
            throw new InvalidOperationException($"{nameof(CategoryBatchSize)} must be greater than 0.");
    }

    [GeneratedRegex(@"```(?:json)?", RegexOptions.IgnoreCase)]
    private static partial Regex JsonCodeFenceRegex();
    private readonly KernelFunction _extractFunction = kernel.CreateFunctionFromPrompt(
            """
            You are a data extraction system for a comprehensive health knowledge base.
            Extract information ONLY from the source text provided below. Do not add
            information from your training data or general medical knowledge that is not
            present in the source text.

            You may use your medical knowledge to interpret and classify the topic type,
            but all extracted content (summary, observations, factors, actions) must be
            traceable to specific statements in the source text.

            The source may contain multiple providers separated by --- with [SourceName]
            headers. Synthesise all sources into one cohesive entry. If sources conflict,
            prefer the more detailed source. Do not merge contradictory claims — include
            only information that is consistent across sources or clearly stated by at
            least one source without being contradicted by another.

            {{$typeInstructions}}

            PROCESS — follow these steps in order:

            {{$nameInstruction}}

            Step 2: For each field below, find the specific statements in the source
            text that support it. If no statements exist for a field, that field MUST
            be empty ([] for arrays, null for strings). Do not infer, guess, or fill
            in from general medical knowledge.

            Step 3: Write the summary by paraphrasing ONLY what the source text states.
            Do not add context, background, or elaboration beyond what is written.

            FIELD RULES:
            - "name": as determined in Step 1
            - "summary": up to 6 sentences paraphrasing what the source text says about
              this topic. Use as many sentences as the source supports — one sentence is
              fine if the source is sparse. Never pad, elaborate, or add background not
              present in the text. Never truncate or end with "..."
            - "observations", "factors", "actions": interpret per the type instructions
              above. Use concise phrasing (not full sentences). Each item MUST be
              traceable to a specific statement in the source text. If the text does not
              cover a field, return an empty array [].
            - "citations": include ONLY if explicitly named in the text (e.g. "NICE
              Guideline NG28", "WHO"). Never fabricate or guess citation names.
              Return [] if none are explicitly named.
            - DO NOT include a "category" field - categories are assigned separately
              using standardized medical classification
            - "tags": up to 8 terms drawn directly from the source text for search
              and metadata. Every tag must appear in or be a direct derivative of a
              word/phrase in the source. Return [] if the source is too sparse.
            - All output in English. Translate if source is not English.

            DO NOT:
            - Add observations, factors, actions, or any list items not stated in the text
            - Invent or guess citations — if the text doesn't name a specific guideline
              or study, return []
            - Pad the summary with general medical knowledge to make it longer
            - Add tags based on what you know about the topic rather than what the text says
            - Rename the topic to a synonym not used in the source text

            Return raw JSON only, no code fences, no explanation.

            Schema:
            {"name":"","summary":"","observations":[],"factors":[],"actions":[],"citations":[],"tags":[]}

            SOURCE TEXT:
            {{$rawText}}
            """,
            functionName: "ExtractMedicalInfo"
        );

    private readonly KernelFunction _classifyFunction = kernel.CreateFunctionFromPrompt(
            """
            You are a medical terminology classifier. You will receive a list of health
            topics, each with a name and optionally a brief description.

            Classify each topic into exactly ONE of the types listed below. Use your
            medical knowledge to interpret the name AND description, and classify
            accordingly. Base your decision on both the provided information and your
            understanding of medical terminology.

            CRITICAL: SKIP NON-MEDICAL AND AMBIGUOUS TOPICS
            DO NOT include topics in your response if they are:
            - Non-medical topics (e.g. Earthquake, Climate Change, Economics, Politics,
              natural disasters, environmental issues, social issues)
            - Ambiguous topics that clearly don't fit any medical category
            Simply OMIT these topics from your JSON response. Only return topics that
            are genuinely health or medical related.

            TYPES (in priority order — choose the first type that fits):
            1. Disease: named diseases, infections, and pathological conditions
               (e.g. Asthma, Diabetes, Pneumonia, Tuberculosis, Malaria)

            2. Disorder: functional or structural disorders not classified as diseases
               (e.g. Sleep Disorder, Eating Disorder, Movement Disorder)

            3. Syndrome: named syndromes (collections of co-occurring symptoms)
               (e.g. Down Syndrome, Irritable Bowel Syndrome, Chronic Fatigue Syndrome)

            4. Symptom: ANY subjective experience, physical sensation, pain, discomfort,
               sign, clinical finding, or observable manifestation of a health condition
               (e.g. Abdominal Pain, Headache, Fever, Nausea, Fatigue, Dizziness, Cough,
               Swelling, Rash, Chest Pain, Back Pain, Shortness of Breath, Bleeding)

            5. Drug: drugs, drug classes, medications, or pharmacological substances
               (e.g. Ibuprofen, Antibiotics, Aspirin, Chemotherapy, Insulin)

            6. Procedure: ANY medical or surgical procedure, operation, therapy,
               intervention, or treatment technique performed BY a healthcare provider,
               including complementary/alternative therapies
               (e.g. Amputation, Transplant, Resection, Biopsy, Catheterisation,
               Dialysis, Physiotherapy, Radiation Therapy, Surgery, Vaccination,
               Acupuncture, Chiropractic, Massage Therapy)

            7. Diagnostic Test: diagnostic tests, lab tests, imaging, screening, or
               examination procedures used to identify conditions
               (e.g. Blood Test, X-Ray, MRI, Colonoscopy, Mammogram, CT Scan)

            8. Vaccine: vaccines or immunisations
               (e.g. MMR Vaccine, Flu Vaccine, COVID-19 Vaccine)

            9. Anatomy: body parts, organs, body systems, tissues, or biological structures
               (e.g. Heart, Liver, Nervous System, Skin, Blood Vessels, Kidney)

            10. Nutrient: vitamins, minerals, supplements, nutrients, and dietary
                substances (e.g. Calcium, Vitamin D, Iron, Omega-3, Folic Acid, Protein)

            11. Mental Health: mental health conditions, psychological disorders,
                emotional states, psychological therapies, and behavioural health topics
                (e.g. Anxiety, Depression, PTSD, Bipolar Disorder, CBT, Schizophrenia)

            12. Lifestyle: health guidance, prevention, wellness, fitness, safety,
                reproductive life events, caregiving topics, and health behaviours
                (e.g. Exercise, Smoking Cessation, Healthy Eating, Sleep Hygiene,
                Pregnancy, Stress Management, Weight Loss)

            DISAMBIGUATION RULES:
            - If the name includes "Pain", "Ache", "Discomfort", "Bleeding", "Swelling",
              "Discharge", or describes a sensation → Symptom
            - If the description mentions surgery, removal, or intervention → Procedure
            - If the description mentions a condition causing impairment → Disease or Disorder
            - If the description mentions performing a test or examination → Diagnostic Test
            - If the name could fit multiple types, use the description to decide
            - If truly ambiguous and not clearly medical → OMIT from response

            IMPORTANT: You MUST return one of the exact type names listed above
            (Disease, Disorder, Syndrome, Symptom, Drug, Procedure, Diagnostic Test,
            Vaccine, Anatomy, Nutrient, Mental Health, Lifestyle).
            Do not return synonyms or variations (e.g. return "Procedure" not "Surgery").

            Return ONLY a raw JSON object mapping each MEDICAL topic name (exactly as
            given) to its type. OMIT non-medical topics entirely. No explanation, no
            code fences.

            Example input: "Asthma", "Earthquake", "Ibuprofen", "Climate Change", "Abdominal Pain"
            Example output: {"Asthma":"Disease","Ibuprofen":"Drug","Abdominal Pain":"Symptom"}

            TOPICS:
            {{$topics}}
            """,
            functionName: "ClassifyTopicNames"
        );

    private readonly KernelFunction _categoryFunction = kernel.CreateFunctionFromPrompt(
            """
            You are a medical category classifier. You will receive a list of health topics
            with their names, types, and summaries. Classify each topic into exactly ONE
            standardized medical category from the list below.

            These categories are based on ICD-10 chapters and medical ontology standards.
            Your classification should be consistent and follow industry guidelines.

            STANDARD MEDICAL CATEGORIES (return EXACTLY as shown):
            1. Infectious & Parasitic Diseases - bacterial, viral, fungal, parasitic infections
            2. Neoplasms - cancers, tumors, malignant and benign growths
            3. Blood & Immune System - blood disorders, immune deficiencies, coagulation issues
            4. Endocrine, Nutritional & Metabolic - hormonal, metabolic, nutritional disorders
            5. Mental & Behavioral - mental health, psychological, behavioral, substance use
            6. Nervous System - brain, spinal cord, peripheral nerves, neurological conditions
            7. Eye & Ear - vision, hearing, ocular and auditory conditions
            8. Circulatory System - heart, blood vessels, cardiovascular conditions
            9. Respiratory System - lungs, airways, breathing conditions
            10. Digestive System - mouth to anus, liver, gallbladder, pancreas
            11. Skin & Subcutaneous Tissue - skin, hair, nails, subcutaneous conditions
            12. Musculoskeletal & Connective Tissue - bones, joints, muscles, ligaments
            13. Genitourinary System - kidneys, bladder, reproductive organs
            14. Pregnancy & Childbirth - pregnancy, labor, postpartum, reproductive health
            15. Perinatal & Congenital - newborn conditions, birth defects, genetic disorders
            16. Symptoms & Signs - general symptoms not specific to a body system
            17. Injury & Poisoning - trauma, wounds, fractures, poisoning, toxicity
            18. External Causes & Factors - environmental, occupational, lifestyle factors
            19. Preventive Care & Screening - health maintenance, screening, prevention
            20. Drugs & Medications - pharmaceutical substances, drug classes
            21. Medical Procedures & Interventions - surgical and non-surgical procedures
            22. Diagnostic & Laboratory - tests, imaging, diagnostic procedures
            23. Nutrition & Dietary - nutrients, diet, supplements, food-related
            24. Health & Wellness - general wellness, fitness, lifestyle, self-care

            TYPE-SPECIFIC MANDATORY MAPPINGS:
            - Type "Drug" → MUST go to "Drugs & Medications"
            - Type "Procedure" → MUST go to "Medical Procedures & Interventions"
            - Type "Diagnostic Test" → MUST go to "Diagnostic & Laboratory"
            - Type "Vaccine" → MUST go to "Preventive Care & Screening"
            - Type "Nutrient" → MUST go to "Nutrition & Dietary"
            - Type "Lifestyle" → MUST go to "Health & Wellness"
            - Type "Mental Health" → MUST go to "Mental & Behavioral"

            Return ONLY a raw JSON object mapping each topic name to its category.
            No explanation, no code fences.

            TOPICS:
            {{$topics}}
            """,
            functionName: "ClassifyTopicCategories"
        );

    private readonly KernelFunction _broaderNameFunction = kernel.CreateFunctionFromPrompt(
            """
            You are a medical terminology expert. You will receive two health topic
            names. Determine which name is the better term to use as the primary
            entry in a medical knowledge base.

            Rules:
            - Both names must describe the SAME medical subject to be merged
            - DISTINCT conditions must NOT be merged, even if related
              (e.g. "Acute Bronchitis" and "Bronchitis" are different scope — return "different")
            - If the two names are synonyms for the EXACT same condition, prefer:
              1. The BROADER term that covers more ground (e.g. "Bronchitis" over "Acute Bronchitis")
              2. Plain English over clinical jargon (e.g. "High Blood Pressure" over "Hypertension")
              3. Standard noun form
            - A specific subtype is NOT a synonym — it is a different entry
              (e.g. "Acute Lymphocytic Leukemia" vs "Leukemia" → "different")
            - If they describe different conditions or different scopes, return "different"

            Return ONLY a raw JSON object with these fields:
            - "preferred": the better name to use, or "different" if they are different subjects
            - "replace": true if the candidate should replace the existing name, false otherwise

            CANDIDATE (newly discovered): {{$candidate}}
            EXISTING (already in database): {{$existing}}
            """,
            functionName: "CompareBroaderName"
        );

    private readonly KernelFunction _matchOriginalNamesFunction = kernel.CreateFunctionFromPrompt(
            """
            You are a medical terminology matcher. You will receive a list of medical topic
            names that have been normalized by an LLM, along with candidate original names
            from MedlinePlus.

            For each normalized name, find the candidate original name that refers to the
            SAME medical condition. The normalized name may use different phrasing, standard
            noun form, or plain English equivalents.

            Return ONLY a raw JSON object mapping each normalized name to its best-matching
            original name. If no candidate matches a normalized name, OMIT it from the result.

            Do not guess — only match when you are confident they refer to the same condition.

            CANDIDATE ORIGINAL NAMES:
            {{$candidates}}

            NORMALIZED NAMES TO MATCH:
            {{$normalizedNames}}
            """,
            functionName: "MatchOriginalNames"
        );

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed record LlmExtractResult
    {
        public string? Name { get; init; }
        public string? Summary { get; init; }
        public List<string>? Observations { get; init; }
        public List<string>? Factors { get; init; }
        public List<string>? Actions { get; init; }
        public List<string>? Citations { get; init; }
        public List<string>? Tags { get; init; }
    }

    private static string GetTypeInstructions(string? topicType)
    {
        return topicType switch
        {
            "Drug" =>
                """
                This topic is a DRUG/MEDICATION. Interpret fields accordingly:
                - "actions": list uses and indications mentioned in the text
                - "observations": list side effects and adverse reactions mentioned in the text
                - "factors": list contraindications and warnings mentioned in the text
                Only include items explicitly stated in the source. Return [] for any
                field not covered by the text.
                """,
            "Procedure" or "Diagnostic Test" =>
                $"""
                This topic is a {topicType.ToUpperInvariant()}. Interpret fields accordingly:
                - "actions": list what conditions it treats or tests for, as stated in the text
                - "observations": list risks or complications if mentioned in the text, otherwise []
                - "factors": list reasons the procedure is needed if stated in the text, otherwise []
                Only include items explicitly stated in the source.
                """,
            "Symptom" =>
                """
                This topic is a SYMPTOM. Interpret fields accordingly:
                - "factors": list conditions or factors that cause this symptom, as stated in the text
                - "actions": list management strategies and remedies mentioned in the text
                - "observations": list associated or related symptoms if mentioned in the text, otherwise []
                Only include items explicitly stated in the source.
                """,
            "Vaccine" =>
                """
                This topic is a VACCINE. Interpret fields accordingly:
                - "actions": list diseases it prevents, as stated in the text
                - "observations": list side effects if mentioned in the text, otherwise []
                - "factors": list contraindications or who should not receive it, as stated in the text
                Only include items explicitly stated in the source.
                """,
            "Anatomy" =>
                """
                This topic is an ANATOMY topic. Interpret fields accordingly:
                - "actions": list treatments mentioned in the text, otherwise []
                - "observations": list conditions or problems mentioned in the text for this body part/system
                - "factors": list risk factors if mentioned in the text, otherwise []
                Only include items explicitly stated in the source.
                """,
            "Nutrient" =>
                """
                This topic is a NUTRIENT/DIETARY SUBSTANCE. Interpret fields accordingly:
                - "actions": list health benefits and medical uses mentioned in the text
                - "observations": list deficiency symptoms or signs of excess mentioned in the text
                - "factors": list dietary sources and factors affecting levels mentioned in the text
                Only include items explicitly stated in the source.
                """,
            "Mental Health" =>
                """
                This topic is a MENTAL HEALTH topic. Interpret fields accordingly:
                - "actions": list therapies, interventions, and management strategies mentioned in the text
                - "observations": list psychological and behavioural symptoms mentioned in the text
                - "factors": list risk factors and contributing causes mentioned in the text
                Only include items explicitly stated in the source.
                """,
            "Lifestyle" =>
                """
                This topic is a LIFESTYLE/WELLNESS topic. Interpret fields accordingly:
                - "actions": list recommendations and strategies mentioned in the text
                - "observations": list health issues or risks discussed in the text
                - "factors": list contributing factors mentioned in the text
                Only include items explicitly stated in the source.
                """,
            _ =>
                """
                Interpret fields with their standard medical meanings:
                - "observations": signs and symptoms mentioned in the text
                - "factors": causes and risk factors mentioned in the text
                - "actions": treatments and management strategies mentioned in the text
                Only include items explicitly stated in the source. Return [] for any
                field not covered by the text.
                """
        };
    }

    public async Task<HealthTopic?> ParseHealthTopicAsync(string rawText, string? topicType = null, string? discoveredName = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            throw new ArgumentException("Raw text cannot be null or empty.", nameof(rawText));

        if (rawText.Length > MaxRawTextSize)
            throw new ArgumentException($"Raw text exceeds maximum size of {MaxRawTextSize} bytes.", nameof(rawText));

        if (discoveredName != null && discoveredName.Length > MaxTopicNameLength)
            throw new ArgumentException($"Topic name exceeds maximum length of {MaxTopicNameLength} characters.", nameof(discoveredName));

        if (!ShouldProcessTopicType(topicType))
        {
            _logger?.LogSkippedTopic(discoveredName ?? "Unknown", $"Filtered topic type: {topicType}");
            return null;
        }

        var typeInstructions = GetTypeInstructions(topicType);

        var nameInstruction = discoveredName is not null
            ? $"""
              Step 1: The discovered topic name is "{discoveredName}". Determine the
              MEDICAL CONDITION described by this page. The name must identify a specific
              diagnosable condition, disease, disorder, syndrome, injury, or symptom —
              never an anatomy term, body part, or vague subject.

              NAMING RULES:
              - Use standard medical NOUN FORM for conditions:
                "Dislocated Shoulder" → "Shoulder Dislocation"
                "Torn Meniscus" → "Meniscus Tear"
                "Collapsed Lung" → "Pneumothorax"
                "Broken Arm" → "Arm Fracture"
              - Drop SEVERITY and TEMPORAL qualifiers that don't change the condition:
                "Acute Bronchitis" → "Bronchitis"
                "Chronic Kidney Disease" → "Kidney Disease"
                "Severe Depression" → "Depression"
                EXCEPTION: Keep well-known abbreviations (COPD, not "Obstructive Pulmonary Disease")
              - KEEP qualifiers that identify a DISTINCT condition:
                "Diabetic Retinopathy" (not just "Retinopathy")
                "Breast Cancer" (not just "Cancer")
                "Type 1 Diabetes" (distinct from Type 2)
                "Gestational Diabetes" (distinct condition)
              - Use PLAIN ENGLISH that a general audience would recognize:
                "Heart Attack" not "Myocardial Infarction"
                "High Blood Pressure" not "Hypertension"
                But use the medical term when it IS the common name:
                "Pneumonia", "Asthma", "Epilepsy"
              - PREFER well-known abbreviations over expanded forms:
                "COPD" not "Chronic Obstructive Pulmonary Disease"
                "HIV" not "Human Immunodeficiency Virus"
                "PTSD" not "Post-Traumatic Stress Disorder"

              FORMATTING:
              - PREFER well-known abbreviations: COPD, HIV, PTSD (not expanded forms)
              - PLURAL form for categories ("Disorders", "Injuries", "Infections") but singular for specific named conditions ("Anemia", "Appendicitis")
              - AMERICAN ENGLISH spelling ("Esophagus" not "Oesophagus")

              Extract information about this condition as described in the source text.
              """
            : """
              Step 1: Identify the MEDICAL CONDITION described in this text. The name
              must identify a specific diagnosable condition, disease, disorder, syndrome,
              injury, or symptom — never an anatomy term, body part, or vague subject.

              NAMING RULES:
              - Use standard medical NOUN FORM: "Shoulder Dislocation" not "Dislocated Shoulder"
              - Drop severity/temporal qualifiers: "Acute Bronchitis" → "Bronchitis"
                EXCEPTION: Keep well-known abbreviations (COPD, not "Obstructive Pulmonary Disease")
              - KEEP qualifiers for DISTINCT conditions: "Breast Cancer", "Type 1 Diabetes"
              - Use PLAIN ENGLISH: "Heart Attack" not "Myocardial Infarction"
                But use medical terms when they ARE the common name: "Pneumonia", "Asthma"

              FORMATTING:
              - PREFER well-known abbreviations: COPD, HIV, PTSD (not expanded forms)
              - PLURAL form for categories ("Disorders", "Injuries", "Infections") but singular for specific named conditions ("Anemia", "Appendicitis")
              - AMERICAN ENGLISH spelling
              """;

        var extractResult = await _kernel.InvokeAsync(
            _extractFunction,
            new() { { "rawText", rawText }, { "typeInstructions", typeInstructions }, { "nameInstruction", nameInstruction } },
            ct
        );

        var extractedJson = extractResult.GetValue<string>();

        if (string.IsNullOrWhiteSpace(extractedJson))
            throw new InvalidOperationException("LLM returned empty result.");

        extractedJson = CleanupJson(extractedJson);

        var model = JsonSerializer.Deserialize<LlmExtractResult>(extractedJson, _jsonOptions)
            ?? throw new InvalidOperationException("Failed to parse returned JSON into extraction result.");

        if (string.IsNullOrWhiteSpace(model.Name))
            throw new InvalidOperationException("LLM returned a topic with no name.");

        if (string.IsNullOrWhiteSpace(model.Summary))
            throw new InvalidOperationException($"LLM returned topic '{model.Name}' with no summary.");

        var topic = new HealthTopic
        {
            Id = Guid.NewGuid(),
            Name = ToTitleCase(model.Name.Trim()),
            Summary = ToSentenceCase(model.Summary),
            Observations = NormalizeList(model.Observations ?? [], ToSentenceCase),
            Factors = NormalizeList(model.Factors ?? [], ToSentenceCase),
            Actions = NormalizeList(model.Actions ?? [], ToSentenceCase),
            Citations = model.Citations ?? [],
            Category = null,
            Tags = NormalizeList(model.Tags ?? [], s => s.ToLowerInvariant()),
            RawSource = rawText,
            LastUpdated = DateTime.UtcNow,
            Version = 1
        };

        return topic;
    }

    private static readonly HashSet<string> ValidTopicTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Disease", "Disorder", "Syndrome", "Symptom", "Drug", "Procedure",
        "Diagnostic Test", "Vaccine", "Anatomy", "Nutrient", "Mental Health",
        "Lifestyle"
    };

    private static readonly HashSet<string> ValidCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "Infectious & Parasitic Diseases", "Neoplasms", "Blood & Immune System",
        "Endocrine, Nutritional & Metabolic", "Mental & Behavioral", "Nervous System",
        "Eye & Ear", "Circulatory System", "Respiratory System", "Digestive System",
        "Skin & Subcutaneous Tissue", "Musculoskeletal & Connective Tissue",
        "Genitourinary System", "Pregnancy & Childbirth", "Perinatal & Congenital",
        "Symptoms & Signs", "Injury & Poisoning", "External Causes & Factors",
        "Preventive Care & Screening", "Drugs & Medications",
        "Medical Procedures & Interventions", "Diagnostic & Laboratory",
        "Nutrition & Dietary", "Health & Wellness"
    };

    private static readonly HashSet<string> FilteredTopicTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Non-Medical", "Other", "Anatomy", "Drug", "Procedure",
        "Diagnostic Test", "Vaccine", "Nutrient", "Lifestyle"
    };

    public bool ShouldProcessTopicType(string? topicType)
    {
        if (string.IsNullOrWhiteSpace(topicType))
            return true;

        return !FilteredTopicTypes.Contains(topicType);
    }

    private static readonly Dictionary<string, string> MandatoryTypeCategoryMappings = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Drug", "Drugs & Medications" },
        { "Procedure", "Medical Procedures & Interventions" },
        { "Diagnostic Test", "Diagnostic & Laboratory" },
        { "Vaccine", "Preventive Care & Screening" },
        { "Nutrient", "Nutrition & Dietary" },
        { "Lifestyle", "Health & Wellness" },
        { "Mental Health", "Mental & Behavioral" }
    };

    private static bool IsValidTypeCategoryPair(string type, string category)
    {
        if (MandatoryTypeCategoryMappings.TryGetValue(type, out var requiredCategory))
            return string.Equals(category, requiredCategory, StringComparison.OrdinalIgnoreCase);

        return true;
    }

    public async Task<Dictionary<string, string>> ClassifyTopicNamesAsync(IReadOnlyList<TopicClassifyInput> topics, CancellationToken ct = default)
    {
        if (topics.Count == 0)
            return [];

        var allClassified = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var batch in topics.Chunk(ClassifyBatchSize))
        {
            try
            {
                var topicsText = string.Join("\n", batch.Select(t =>
                {
                    var snippet = TruncateToSentence(t.SummarySnippet, MaxSnippetLength);
                    return string.IsNullOrWhiteSpace(snippet)
                        ? $"- {t.Name}"
                        : $"- {t.Name}: {snippet}";
                }));

                var result = await _kernel.InvokeAsync(
                    _classifyFunction,
                    new() { { "topics", topicsText } },
                    ct
                );

                var json = CleanupJson(result.GetValue<string>() ?? "{}");
                var classified = JsonSerializer.Deserialize<Dictionary<string, string>>(json, _jsonOptions) ?? [];

                if (classified.Count == 0 && batch.Length > 0)
                {
                    _logger?.LogBatchError(nameof(ClassifyTopicNamesAsync), batch.Length,
                        new InvalidOperationException("LLM returned empty or unparseable JSON for classify batch"));
                }

                foreach (var kvp in classified)
                {
                    if (!batch.Any(t => string.Equals(t.Name, kvp.Key, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    if (ValidTopicTypes.Contains(kvp.Value))
                        allClassified[kvp.Key] = kvp.Value;
                    else
                        _logger?.LogInvalidClassification(kvp.Key, kvp.Value);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogBatchError(nameof(ClassifyTopicNamesAsync), batch.Length, ex);
                continue;
            }
        }

        return allClassified;
    }

    public async Task<Dictionary<string, string>> ClassifyTopicCategoriesAsync(IReadOnlyList<TopicCategoryInput> topics, CancellationToken ct = default)
    {
        if (topics.Count == 0)
            return [];

        var allCategorized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var batch in topics.Chunk(CategoryBatchSize))
        {
            try
            {
                var topicsText = string.Join("\n", batch.Select(t =>
                {
                    var snippet = TruncateToSentence(t.SummarySnippet, MaxSnippetLength);
                    var description = string.IsNullOrWhiteSpace(snippet) ? "" : $": {snippet}";
                    return $"- {t.Name} (Type: {t.TopicType}){description}";
                }));

                var result = await _kernel.InvokeAsync(
                    _categoryFunction,
                    new() { { "topics", topicsText } },
                    ct
                );

                var json = CleanupJson(result.GetValue<string>() ?? "{}");
                var categorized = JsonSerializer.Deserialize<Dictionary<string, string>>(json, _jsonOptions) ?? [];

                if (categorized.Count == 0 && batch.Length > 0)
                {
                    _logger?.LogBatchError(nameof(ClassifyTopicCategoriesAsync), batch.Length,
                        new InvalidOperationException("LLM returned empty or unparseable JSON for category batch"));
                }

                foreach (var kvp in categorized)
                {
                    var inputTopic = batch.FirstOrDefault(t => string.Equals(t.Name, kvp.Key, StringComparison.OrdinalIgnoreCase));
                    if (inputTopic == null)
                        continue;

                    if (!ValidCategories.Contains(kvp.Value))
                        continue;

                    if (!IsValidTypeCategoryPair(inputTopic.TopicType, kvp.Value))
                    {
                        _logger?.LogInvalidCategoryPair(kvp.Key, inputTopic.TopicType, kvp.Value);
                        continue;
                    }

                    allCategorized[kvp.Key] = kvp.Value;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogBatchError(nameof(ClassifyTopicCategoriesAsync), batch.Length, ex);
                continue;
            }
        }

        return allCategorized;
    }

    private sealed record BroaderNameLlmResult
    {
        public string? Preferred { get; init; }
        public bool Replace { get; init; }
    }

    public async Task<BroaderNameResult> CompareBroaderNameAsync(
        string candidate, string existing, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            throw new ArgumentException("Candidate name cannot be null or empty.", nameof(candidate));

        if (string.IsNullOrWhiteSpace(existing))
            throw new ArgumentException("Existing name cannot be null or empty.", nameof(existing));

        var result = await _kernel.InvokeAsync(
            _broaderNameFunction,
            new() { { "candidate", candidate }, { "existing", existing } },
            ct
        );

        var json = CleanupJson(result.GetValue<string>() ?? "{}");
        var parsed = JsonSerializer.Deserialize<BroaderNameLlmResult>(json, _jsonOptions);

        if (parsed is null || string.IsNullOrWhiteSpace(parsed.Preferred))
            return new BroaderNameResult(existing, false);

        // LLM explicitly said the topics are different subjects — return the candidate
        // name (not existing) so the caller can distinguish this from "keep existing".
        if (string.Equals(parsed.Preferred, "different", StringComparison.OrdinalIgnoreCase))
            return new BroaderNameResult(candidate, false);

        return new BroaderNameResult(parsed.Preferred, parsed.Replace);
    }

    public async Task<Dictionary<string, string>> MatchOriginalNamesAsync(IReadOnlyList<string> normalizedNames, IReadOnlyList<string> candidateNames, CancellationToken ct = default)
    {
        if (normalizedNames.Count == 0 || candidateNames.Count == 0)
            return [];

        var allMatched = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var candidatesText = string.Join(", ", candidateNames.Select(c => $"\"{c}\""));

        foreach (var batch in normalizedNames.Chunk(ClassifyBatchSize))
        {
            try
            {
                var namesText = string.Join("\n", batch.Select(n => $"- {n}"));

                var result = await _kernel.InvokeAsync(
                    _matchOriginalNamesFunction,
                    new() { { "candidates", candidatesText }, { "normalizedNames", namesText } },
                    ct
                );

                var json = CleanupJson(result.GetValue<string>() ?? "{}");
                var matched = JsonSerializer.Deserialize<Dictionary<string, string>>(json, _jsonOptions) ?? [];

                foreach (var kvp in matched)
                {
                    if (!batch.Any(n => string.Equals(n, kvp.Key, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    allMatched[kvp.Key] = kvp.Value;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogBatchError(nameof(MatchOriginalNamesAsync), batch.Length, ex);
                continue;
            }
        }

        return allMatched;
    }

    private static string? TruncateToSentence(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        if (text.Length <= maxLength)
            return text.Trim();

        var region = text[..maxLength];
        var lastPeriod = region.LastIndexOfAny(['.', '!', '?']);
        if (lastPeriod > 0)
            return region[..(lastPeriod + 1)].Trim();

        var lastSpace = region.LastIndexOf(' ');
        return lastSpace > 0 ? region[..lastSpace].Trim() : region.Trim();
    }

    private static string ToTitleCase(string text) => TopicHelpers.ToTitleCase(text);

    private static string ToSentenceCase(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        var trimmed = text.Trim();
        return char.ToUpperInvariant(trimmed[0]) + trimmed[1..];
    }

    private static List<string> NormalizeList(List<string> items, Func<string, string> transform) =>
        [.. items.Where(s => !string.IsNullOrWhiteSpace(s))
             .Select(s => transform(s.Trim()))
             .Distinct(StringComparer.OrdinalIgnoreCase)];

    private static string CleanupJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return "{}";

        json = JsonCodeFenceRegex().Replace(json, "");
        json = json.Replace("`", "");
        json = json.Trim();

        var firstBrace = json.IndexOf('{');
        var lastBrace = json.LastIndexOf('}');

        if (firstBrace >= 0 && lastBrace > firstBrace)
        {
            json = json[firstBrace..(lastBrace + 1)];
        }

        json = json.TrimEnd();
        if (!json.EndsWith('}') && json.StartsWith('{'))
        {
            var (openBraces, closeBraces) = (0, 0);
            foreach (var c in json)
            {
                if (c == '{')
                    openBraces++;
                else if (c == '}')
                    closeBraces++;
            }

            for (var i = 0; i < openBraces - closeBraces; i++)
                json += "}";
        }

        if (!json.StartsWith('{') || !json.EndsWith('}'))
            return "{}";

        try
        {
            using var doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return "{}";
        }

        return json;
    }
}
