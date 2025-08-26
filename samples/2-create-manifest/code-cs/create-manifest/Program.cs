// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace create_manifest
{
    using Microsoft.PowerPlatform.Dataverse.Client;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Threading.Tasks;

    class Program
    {
        // Padbestanden en config (TODO: van hardcoded naar config/args indien gewenst)

        // Dit is de map van het project (create-manifest)
        private static readonly string CreateManifestRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
        // Één niveau hoger: code-cs (naast create-manifest)
        private static readonly string CodeCsRoot = Path.GetFullPath(Path.Combine(CreateManifestRoot, ".."));

        //Mappen
        private static readonly string BaseOutputRoot = Path.Combine(CodeCsRoot, "cdm-out");
        private static readonly string SchemaFilesSource = Path.Combine(CodeCsRoot, "needed-files");
        private const string SchemaVersion = "1.1.0";

        // Leveranciers (prefix, label)
        static readonly List<(string Prefix, string Label)> Suppliers = new()
        {
            ("nrq",  "Norriq"),
            ("svc",  "Savaco"),
            ("ccp",  "Valantic"),
            ("sgw",  "Sint-Gillis-Waas"),
            ("ccsp", "BeginPrefix"),
            ("qbx",  "Qubix"),
            ("msdyn","MS Dynamics"),
            ("msdynce","MS Dynamics Customer"),
            ("msdyncrm","MS Dynamics CRM"),
            ("msdynmkt","MS Dynamics Marketting"),
            ("msfp","MS Forms Pro"),
            ("mspcat","MS Solution Package Catalogue"),
            ("aal", "Aalter"),
            ("lb", "LB365 Algemeen"),
            ("mspp","MS Power Pages")
        };

        // Root manifest
        private const string RootManifestName = "LB365";

        // 1e laag submanifests (exact de namen die je gaf)
        private static readonly string[] DomainNames = new[]
        {
            "GENERAL",
            "MELDINGEN",
            "KLACHTEN",
            "DIAGRAM POSTREGISTRATIE",
            "WEBCONTENT",
            "IPDC",
            "PRODUCTENCATALOGUS P30",
            "CONTRACTEN",
            "SUBSIDIES",
            "PROCESMANAGER",
            "VERGADERAPP",
            "FUNCTIONERING",
            "VERGOEDING",
            "VERGADERBEHEER",
            "BEHEER VAN ZAKEN EN DOSSIERS",
            "CCSP ORGANISATIES",
            "CCSP CONTACTPERSONEN",
            "CCSP PERSONEN",
            "EVENEMENTEN",
            "INNAME OPENBAAR DOMEIN"
        };

        // Toewijzing van entities per domein (logical names). Niet-gematchte → "GENERAL".
        private static readonly Dictionary<string, HashSet<string>> SubManifestEntitySets =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["GENERAL"] = new(StringComparer.OrdinalIgnoreCase),
                ["MELDINGEN"] = new(StringComparer.OrdinalIgnoreCase),
                ["KLACHTEN"] = new(StringComparer.OrdinalIgnoreCase),
                ["DIAGRAM POSTREGISTRATIE"] = new(StringComparer.OrdinalIgnoreCase),
                ["WEBCONTENT"] = new(StringComparer.OrdinalIgnoreCase),
                ["IPDC"] = new(StringComparer.OrdinalIgnoreCase),
                ["PRODUCTENCATALOGUS P30"] = new(StringComparer.OrdinalIgnoreCase),
                ["CONTRACTEN"] = new(StringComparer.OrdinalIgnoreCase),
                ["SUBSIDIES"] = new(StringComparer.OrdinalIgnoreCase),
                ["PROCESMANAGER"] = new(StringComparer.OrdinalIgnoreCase),
                ["VERGADERAPP"] = new(StringComparer.OrdinalIgnoreCase),
                ["FUNCTIONERING"] = new(StringComparer.OrdinalIgnoreCase),
                ["VERGOEDING"] = new(StringComparer.OrdinalIgnoreCase),
                ["VERGADERBEHEER"] = new(StringComparer.OrdinalIgnoreCase),
                ["BEHEER VAN ZAKEN EN DOSSIERS"] = new(StringComparer.OrdinalIgnoreCase),
                ["CCSP ORGANISATIES"] = new(StringComparer.OrdinalIgnoreCase),
                ["CCSP CONTACTPERSONEN"] = new(StringComparer.OrdinalIgnoreCase),
                ["CCSP PERSONEN"] = new(StringComparer.OrdinalIgnoreCase),
                ["EVENEMENTEN"] = new(StringComparer.OrdinalIgnoreCase),
                ["INNAME OPENBAAR DOMEIN"] = new(StringComparer.OrdinalIgnoreCase)
            };

        // Herkenning van custom entiteiten (prefixes)
        private static readonly string[] KnownCustomPrefixes =
        {
            "ccp_", "nrq_", "svc_", "ccsp_", "qbx_", "sgw_", "aal_", "lb_", "msdyn", "msdynce", "msdyncrm", "msdynmkt", "msfp", "mspcat", "mspp"
        };

        static async Task Main(string[] args)
        {
            Console.WriteLine("Mappencontrole...");
            Console.WriteLine($"CreateManifestRoot: {CreateManifestRoot}");
            Console.WriteLine($"CodeCsRoot       : {CodeCsRoot}");
            Console.WriteLine($"BaseOutputRoot   : {BaseOutputRoot}");
            Console.WriteLine($"SchemaFilesSource: {SchemaFilesSource}");
            Console.WriteLine();

            // --- Stap 1: Keuze menu ---
            Console.WriteLine("Stap 1: Ophalen metadata uit Dataverse en wegschrijven (gefilterd)");
            Console.WriteLine("Kies wat je wil exporteren:");
            Console.WriteLine("  1) Alle entiteiten");
            for (int i = 0; i < Suppliers.Count; i++)
                Console.WriteLine($"  {i + 2}) Prefix: {Suppliers[i].Prefix} ({Suppliers[i].Label})");
            Console.Write("Maak je keuze (getal): ");
            string? input = Console.ReadLine();
            if (!int.TryParse(input, out int choice))
            {
                Console.WriteLine("Ongeldige invoer — standaard: Alle entiteiten.");
                choice = 1;
            }

            string? prefix = null;
            string suffix;
            if (choice == 1)
            {
                prefix = null; // alles
                suffix = "all";
            }
            else
            {
                int idx = choice - 2;
                if (idx < 0 || idx >= Suppliers.Count)
                {
                    Console.WriteLine("Onbekende keuze — standaard: Alle entiteiten.");
                    prefix = null;
                    suffix = "all";
                }
                else
                {
                    prefix = Suppliers[idx].Prefix;
                    suffix = prefix.ToLowerInvariant();
                }
            }

            string outDir = Path.Combine(BaseOutputRoot, suffix);
            EnsureEmptyDirectory(outDir);

            // foundations + cdsConcepts kopiëren
            CopySchemaFilesTo(outDir);

            // Bestandsnaam voor gefilterde export
            string exportPath = Path.Combine(outDir, $"entities-{suffix}.json");

            // --- Stap 1: Exporteren met filter (prefix of alles) ---
            string connectionString = PromptConnectionStringIfEmpty(""); // leeg laten → vraagt in console
            bool ok = await ExportDataverseMetadataAsync(connectionString, prefix, exportPath);
            if (!ok)
            {
                Console.WriteLine("Export mislukt of geen entiteiten gevonden. Stoppen.");
                return;
            }

            // --- Stap 2: CDM genereren ---
            if (prefix == null)
            {
                Console.WriteLine("Stap 2: Hiërarchie (LB365/Domeinen/Standard-Custom) genereren...");
                GenerateCdmHierarchy(exportPath, outDir, SchemaVersion, forceAllDomains: true);
            }
            else
            {
                Console.WriteLine("Stap 2: Leveranciermap genereren...");
                GenerateCdmFlat(exportPath, outDir, SchemaVersion);
            }

            Console.WriteLine();
            Console.WriteLine($"Klaar. Bestanden in: {outDir}");
            Console.WriteLine();
        }

        // Zorgt dat een map leeg is (verwijdert indien aanwezig, maakt opnieuw aan).
        private static void EnsureEmptyDirectory(string path)
        {
            if (Directory.Exists(path))
            {
                Console.WriteLine($"Map gevonden: {path} — inhoud wordt opgeruimd...");
                Directory.Delete(path, recursive: true);
            }
            Directory.CreateDirectory(path);
            Console.WriteLine($"Nieuwe map aangemaakt: {path}");
            Console.WriteLine();
        }

        // Interactieve prompt als je de connection string niet hard meegeeft
        private static string PromptConnectionStringIfEmpty(string connectionString)
        {
            if (!string.IsNullOrWhiteSpace(connectionString))
                return connectionString.Trim();

            Console.Write("Dataverse connection string (wordt niet opgeslagen): ");
            var entered = Console.ReadLine()?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(entered))
                throw new InvalidOperationException("Geen connection string opgegeven.");
            return entered;
        }

        /// Exporteert Dataverse metadata en filtert meteen op prefix (of alles).
        /// Neemt ook relaties (1:N / N:1) mee in de export.
        private static async Task<bool> ExportDataverseMetadataAsync(string connectionString, string? prefixFilter, string exportPath)
        {
            var serviceClient = new ServiceClient(connectionString);
            if (!serviceClient.IsReady)
            {
                Console.WriteLine("Verbinding met Dataverse mislukt.");
                return false;
            }
            Console.WriteLine("Verbinding met Dataverse succesvol.");
            Console.WriteLine();

            var solutionExport = new SolutionExport
            {
                SolutionName = prefixFilter is null ? "All" : prefixFilter,
                Entities = new List<SolutionEntity>(),
                Relationships = new List<SolutionRelationship>()
            };

            var request = new Microsoft.Xrm.Sdk.Messages.RetrieveAllEntitiesRequest()
            {
                EntityFilters = Microsoft.Xrm.Sdk.Metadata.EntityFilters.Entity | Microsoft.Xrm.Sdk.Metadata.EntityFilters.Attributes | Microsoft.Xrm.Sdk.Metadata.EntityFilters.Relationships,
                RetrieveAsIfPublished = true
            };
            var response = (Microsoft.Xrm.Sdk.Messages.RetrieveAllEntitiesResponse)serviceClient.Execute(request);

            int total = 0, matched = 0;

            // Entities + attributes
            foreach (var entityMetadata in response.EntityMetadata)
            {
                total++;
                string ln = entityMetadata.LogicalName ?? string.Empty;

                if (!IncludeByPrefix(prefixFilter, ln)) continue;

                matched++;

                var attributes = new List<SolutionAttribute>();
                foreach (var attr in entityMetadata.Attributes)
                {
                    attributes.Add(new SolutionAttribute
                    {
                        Name = attr.LogicalName,
                        Type = attr.AttributeType?.ToString() ?? "String"
                    });
                }

                solutionExport.Entities.Add(new SolutionEntity
                {
                    LogicalName = ln,
                    PrimaryId = entityMetadata.PrimaryIdAttribute ?? "",
                    Attributes = attributes
                });
            }

            // Relationships (1:N en N:1)
            foreach (var em in response.EntityMetadata)
            {
                foreach (var rel in em.OneToManyRelationships ?? Array.Empty<Microsoft.Xrm.Sdk.Metadata.OneToManyRelationshipMetadata>())
                {
                    string referencingEntity = rel.ReferencingEntity ?? "";
                    string referencedEntity = rel.ReferencedEntity ?? "";
                    string fkAttr = rel.ReferencingAttribute ?? "";
                    string pkAttr = rel.ReferencedAttribute ?? "";
                    string schemaName = rel.SchemaName ?? $"{referencingEntity}_{referencedEntity}";

                    if (!IncludeByPrefix(prefixFilter, referencingEntity)) continue;

                    solutionExport.Relationships.Add(new SolutionRelationship
                    {
                        Name = schemaName,
                        FromEntity = referencingEntity,
                        FromAttribute = fkAttr,
                        ToEntity = referencedEntity,
                        ToAttribute = pkAttr
                    });
                }

                foreach (var rel in em.ManyToOneRelationships ?? Array.Empty<Microsoft.Xrm.Sdk.Metadata.OneToManyRelationshipMetadata>())
                {
                    string referencingEntity = rel.ReferencingEntity ?? "";
                    string referencedEntity = rel.ReferencedEntity ?? "";
                    string fkAttr = rel.ReferencingAttribute ?? "";
                    string pkAttr = rel.ReferencedAttribute ?? "";
                    string schemaName = rel.SchemaName ?? $"{referencingEntity}_{referencedEntity}";

                    if (!IncludeByPrefix(prefixFilter, referencingEntity)) continue;

                    solutionExport.Relationships.Add(new SolutionRelationship
                    {
                        Name = schemaName,
                        FromEntity = referencingEntity,
                        FromAttribute = fkAttr,
                        ToEntity = referencedEntity,
                        ToAttribute = pkAttr
                    });
                }
            }

            Console.WriteLine($"Gefilterd op prefix: {(prefixFilter ?? "<all>")}");
            Console.WriteLine($"Totaal entiteiten: {total} | Geselecteerd: {matched}");
            if (matched > 0)
            {
                int show = Math.Min(10, solutionExport.Entities.Count);
                Console.WriteLine("Voorbeeld(en):");
                for (int i = 0; i < show; i++)
                    Console.WriteLine($"  - {solutionExport.Entities[i].LogicalName}");
            }
            Console.WriteLine($"Relaties verzameld: {solutionExport.Relationships.Count}");
            Console.WriteLine();

            if (solutionExport.Entities.Count == 0)
            {
                Console.WriteLine("Geen entiteiten gevonden voor de gekozen filter.");
                return false;
            }

            string json = JsonConvert.SerializeObject(solutionExport, Formatting.Indented);
            File.WriteAllText(exportPath, json);
            Console.WriteLine($"Metadata (incl. relaties) geëxporteerd naar {exportPath}");
            return true;
        }

        /// Hiërarchie (ALL): LB365 → Domeinen → Standard/Custom
        /// Maakt ALLE domeinen en leafs aan (ook als ze leeg zijn).
        private static void GenerateCdmHierarchy(string inputPath, string outputDir, string schemaVersion, bool forceAllDomains)
        {
            var json = JObject.Parse(File.ReadAllText(inputPath));
            var entities = (JArray)json["Entities"]!;
            var rels = (JArray?)json["Relationships"];

            var entityPlacement = new Dictionary<string, (string EntityName, string LeafKey, string LeafDir)>(StringComparer.OrdinalIgnoreCase);

            foreach (var entityNode in entities)
            {
                string logicalName = entityNode["LogicalName"]!.ToString();
                string entityName = ToPascal(logicalName);
                string domain = PickDomainFor(logicalName);
                string category = IsCustom(logicalName) ? "Custom" : "Standard";
                string leafKey = $"{domain}/{category}";
                string leafDir = Path.Combine(outputDir, domain, category);

                entityPlacement[logicalName] = (entityName, leafKey, leafDir);
            }

            var leafEntities = new Dictionary<string, List<(string EntityName, string EntityDocPath)>>(StringComparer.OrdinalIgnoreCase);

            foreach (var kv in entityPlacement)
            {
                string logicalName = kv.Key;
                var (entityName, leafKey, leafDir) = kv.Value;

                Directory.CreateDirectory(leafDir);

                var entityNode = (JObject)entities.First(e =>
                    string.Equals(e["LogicalName"]!.ToString(), logicalName, StringComparison.OrdinalIgnoreCase));

                string primaryId = (entityNode["PrimaryId"]?.ToString() ?? "").Trim();

                var attrs = new JArray();
                var csvHeader = new StringBuilder();

                foreach (var attr in (JArray)entityNode["Attributes"]!)
                {
                    string name = attr["Name"]!.ToString();
                    string type = attr["Type"]?.ToString() ?? "String";
                    string cdmType = MapToCdmType(type);

                    JArray? attrTraits = null;
                    if (!string.IsNullOrWhiteSpace(primaryId) &&
                        string.Equals(name, primaryId, StringComparison.OrdinalIgnoreCase))
                    {
                        attrTraits ??= new JArray();
                        attrTraits.Add(new JObject { ["traitReference"] = "means.identity.entityId" });
                    }

                    var attrObj = new JObject
                    {
                        ["name"] = name,
                        ["dataType"] = new JObject { ["dataTypeReference"] = cdmType }
                    };
                    if (attrTraits != null && attrTraits.Count > 0)
                        attrObj["appliedTraits"] = attrTraits;

                    attrs.Add(attrObj);

                    if (csvHeader.Length > 0) csvHeader.Append(',');
                    csvHeader.Append(name);
                }

                var entityDef = new JObject
                {
                    ["entityName"] = entityName,
                    ["extendsEntity"] = new JObject { ["entityReference"] = "CdmEntity" },
                    ["hasAttributes"] = attrs
                };

                var entityDoc = new JObject
                {
                    ["$schema"] = "cdm:/schema.cdm.json",
                    ["jsonSchemaSemanticVersion"] = schemaVersion,
                    ["imports"] = new JArray(new JObject { ["corpusPath"] = "cdm:/foundations.cdm.json" }),
                    ["definitions"] = new JArray(entityDef)
                };

                string entityFile = Path.Combine(leafDir, $"{entityName}.cdm.json");
                File.WriteAllText(entityFile, JsonConvert.SerializeObject(entityDoc, Formatting.Indented));

                string entityDataDir = Path.Combine(leafDir, entityName);
                Directory.CreateDirectory(entityDataDir);
                string csvPath = Path.Combine(entityDataDir, "partition-data.csv");
                File.WriteAllText(csvPath, csvHeader.ToString() + Environment.NewLine);

                if (!leafEntities.TryGetValue(leafKey, out var list))
                {
                    list = new List<(string, string)>();
                    leafEntities[leafKey] = list;
                }
                list.Add((entityName, $"{entityName}.cdm.json"));
            }

            var leafRels = new Dictionary<string, List<(string FromEntityPath, string FromAttr, string ToEntityPath, string ToAttr)>>(StringComparer.OrdinalIgnoreCase);

            // Init voor alle leafs als we alles forceren
            if (forceAllDomains)
            {
                foreach (var domain in DomainNames)
                {
                    leafRels[$"{domain}/Standard"] = new();
                    leafRels[$"{domain}/Custom"] = new();
                }
            }
            foreach (var key in leafEntities.Keys)
                if (!leafRels.ContainsKey(key)) leafRels[key] = new();

            if (rels != null)
            {
                foreach (var r in rels)
                {
                    string fromLogical = r["FromEntity"]?.ToString() ?? "";
                    string toLogical = r["ToEntity"]?.ToString() ?? "";
                    string fromAttr = r["FromAttribute"]?.ToString() ?? "";
                    string toAttr = r["ToAttribute"]?.ToString() ?? "";

                    if (string.IsNullOrWhiteSpace(fromLogical) || string.IsNullOrWhiteSpace(toLogical) ||
                        string.IsNullOrWhiteSpace(fromAttr) || string.IsNullOrWhiteSpace(toAttr))
                        continue;

                    if (!entityPlacement.TryGetValue(fromLogical, out var fromInfo)) continue;
                    if (!entityPlacement.TryGetValue(toLogical, out var toInfo)) continue;

                    if (!string.Equals(fromInfo.LeafKey, toInfo.LeafKey, StringComparison.OrdinalIgnoreCase))
                        continue;

                    string fromEntityPath = $"{fromInfo.EntityName}.cdm.json/{fromInfo.EntityName}";
                    string toEntityPath = $"{toInfo.EntityName}.cdm.json/{toInfo.EntityName}";
                    leafRels[fromInfo.LeafKey].Add((fromEntityPath, fromAttr, toEntityPath, toAttr));
                }
            }

            var writtenDomains = new List<string>();
            foreach (var domain in DomainNames)
            {
                string domainDir = Path.Combine(outputDir, domain);
                string standardKey = $"{domain}/Standard";
                string customKey = $"{domain}/Custom";

                bool hasStandard = leafEntities.TryGetValue(standardKey, out var stdList) && stdList.Count > 0;
                bool hasCustom = leafEntities.TryGetValue(customKey, out var cstList) && cstList.Count > 0;

                if (forceAllDomains || hasStandard || hasCustom)
                {
                    WriteDomainManifest(domainDir, domain, schemaVersion, writeStandard: true, writeCustom: true);

                    // Standard leaf (ook leeg aanmaken)
                    string stdLeafDir = Path.Combine(domainDir, "Standard");
                    var stdEntities = hasStandard ? leafEntities[standardKey] : Enumerable.Empty<(string, string)>();
                    var stdRels = leafRels.TryGetValue(standardKey, out var srel) ? srel : Enumerable.Empty<(string, string, string, string)>();
                    WriteLeafManifest(stdLeafDir, "Standard", schemaVersion, stdEntities, stdRels);

                    // Custom leaf (ook leeg aanmaken)
                    string cstLeafDir = Path.Combine(domainDir, "Custom");
                    var cstEntities = hasCustom ? leafEntities[customKey] : Enumerable.Empty<(string, string)>();
                    var cstRels = leafRels.TryGetValue(customKey, out var crel) ? crel : Enumerable.Empty<(string, string, string, string)>();
                    WriteLeafManifest(cstLeafDir, "Custom", schemaVersion, cstEntities, cstRels);

                    writtenDomains.Add(domain);
                }
            }

            // Root manifest met alle domeinen (forceAllDomains → alles, anders enkel geschreven)
            var roots = forceAllDomains ? DomainNames : writtenDomains.ToArray();
            WriteRootManifest(outputDir, schemaVersion, roots);
        }

        /// Platte structuur (prefix): oude werkwijze met één default.manifest.cdm.json
        private static void GenerateCdmFlat(string inputPath, string outputDir, string schemaVersion)
        {
            var json = JObject.Parse(File.ReadAllText(inputPath));
            var entities = (JArray)json["Entities"]!;
            var rels = (JArray?)json["Relationships"];

            var manifest = new JObject
            {
                ["manifestName"] = "default",
                ["jsonSchemaSemanticVersion"] = schemaVersion,
                ["entities"] = new JArray(),
                ["relationships"] = new JArray(),
                ["imports"] = new JArray
                {
                    new JObject { ["corpusPath"] = "cdm:/core/cdsConcepts.cdm.json" },
                    new JObject { ["corpusPath"] = "cdm:/foundations.cdm.json" }
                }
            };

            foreach (var entityNode in entities)
            {
                string logicalName = entityNode["LogicalName"]!.ToString();
                string entityName = ToPascal(logicalName);
                string primaryId = (entityNode["PrimaryId"]?.ToString() ?? "").Trim();

                var attrs = new JArray();
                var csvHeader = new StringBuilder();

                foreach (var attr in (JArray)entityNode["Attributes"]!)
                {
                    string name = attr["Name"]!.ToString();
                    string type = attr["Type"]?.ToString() ?? "String";
                    string cdmType = MapToCdmType(type);

                    JArray? attrTraits = null;
                    if (!string.IsNullOrWhiteSpace(primaryId) &&
                        string.Equals(name, primaryId, StringComparison.OrdinalIgnoreCase))
                    {
                        attrTraits ??= new JArray();
                        attrTraits.Add(new JObject { ["traitReference"] = "means.identity.entityId" });
                    }

                    var attrObj = new JObject
                    {
                        ["name"] = name,
                        ["dataType"] = new JObject { ["dataTypeReference"] = cdmType }
                    };
                    if (attrTraits != null && attrTraits.Count > 0)
                        attrObj["appliedTraits"] = attrTraits;

                    attrs.Add(attrObj);

                    if (csvHeader.Length > 0) csvHeader.Append(',');
                    csvHeader.Append(name);
                }

                var entityDef = new JObject
                {
                    ["entityName"] = entityName,
                    ["extendsEntity"] = new JObject { ["entityReference"] = "CdmEntity" },
                    ["hasAttributes"] = attrs
                };

                var entityDoc = new JObject
                {
                    ["$schema"] = "cdm:/schema.cdm.json",
                    ["jsonSchemaSemanticVersion"] = schemaVersion,
                    ["imports"] = new JArray(new JObject { ["corpusPath"] = "cdm:/foundations.cdm.json" }),
                    ["definitions"] = new JArray(entityDef)
                };

                string entityFile = Path.Combine(outputDir, $"{entityName}.cdm.json");
                File.WriteAllText(entityFile, JsonConvert.SerializeObject(entityDoc, Formatting.Indented));

                string entityFolder = Path.Combine(outputDir, entityName);
                Directory.CreateDirectory(entityFolder);
                string csvPath = Path.Combine(entityFolder, "partition-data.csv");
                File.WriteAllText(csvPath, csvHeader.ToString() + Environment.NewLine);

                ((JArray)manifest["entities"]!).Add(new JObject
                {
                    ["type"] = "LocalEntity",
                    ["entityName"] = entityName,
                    ["entityPath"] = $"{entityName}.cdm.json/{entityName}",
                    ["dataPartitions"] = new JArray(new JObject
                    {
                        ["name"] = $"{entityName}-data-description",
                        ["location"] = $"{entityName}/partition-data.csv",
                        ["appliedTraits"] = new JArray(new JObject
                        {
                            ["traitReference"] = "is.partition.format.CSV",
                            ["arguments"] = new JArray(
                                new JObject { ["name"] = "columnHeaders", ["value"] = "true" },
                                new JObject { ["name"] = "delimiter", ["value"] = "," }
                            )
                        })
                    })
                });
            }

            if (rels != null)
            {
                foreach (var r in rels)
                {
                    string fromEntityLogical = r["FromEntity"]?.ToString() ?? "";
                    string toEntityLogical = r["ToEntity"]?.ToString() ?? "";
                    string fromAttribute = r["FromAttribute"]?.ToString() ?? "";
                    string toAttribute = r["ToAttribute"]?.ToString() ?? "";
                    string fromEntityName = ToPascal(fromEntityLogical);
                    string toEntityName = ToPascal(toEntityLogical);

                    if (string.IsNullOrWhiteSpace(fromEntityName) ||
                        string.IsNullOrWhiteSpace(toEntityName) ||
                        string.IsNullOrWhiteSpace(fromAttribute) ||
                        string.IsNullOrWhiteSpace(toAttribute))
                        continue;

                    ((JArray)manifest["relationships"]!).Add(new JObject
                    {
                        ["fromEntity"] = $"{fromEntityName}.cdm.json/{fromEntityName}",
                        ["fromEntityAttribute"] = fromAttribute,
                        ["toEntity"] = $"{toEntityName}.cdm.json/{toEntityName}",
                        ["toEntityAttribute"] = toAttribute
                    });
                }
            }

            string manifestFile = Path.Combine(outputDir, "default.manifest.cdm.json");
            File.WriteAllText(manifestFile, JsonConvert.SerializeObject(manifest, Formatting.Indented));
        }

        // ===== Helpers =====

        private static bool IncludeByPrefix(string? prefix, string logicalName)
            => string.IsNullOrWhiteSpace(prefix)
               || logicalName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
               || logicalName.StartsWith(prefix + "_", StringComparison.OrdinalIgnoreCase);

        private static void CopySchemaFilesTo(string destRoot)
        {
            // foundations.cdm.json
            string srcFoundations = Path.Combine(SchemaFilesSource, "foundations.cdm.json");
            string dstFoundations = Path.Combine(destRoot, "foundations.cdm.json");
            if (File.Exists(srcFoundations))
            {
                File.Copy(srcFoundations, dstFoundations, overwrite: true);
            }
            else
            {
                Console.WriteLine("foundations.cdm.json niet gevonden in needed-files");
            }

            // core/cdsConcepts.cdm.json
            string srcCore = Path.Combine(SchemaFilesSource, "core", "cdsConcepts.cdm.json");
            string dstCoreDir = Path.Combine(destRoot, "core");
            Directory.CreateDirectory(dstCoreDir);
            string dstCore = Path.Combine(dstCoreDir, "cdsConcepts.cdm.json");
            if (File.Exists(srcCore))
            {
                File.Copy(srcCore, dstCore, overwrite: true);
            }
            else
            {
                Console.WriteLine("core/cdsConcepts.cdm.json niet gevonden in needed-files");
            }
        }

        private static string ToPascal(string logical) =>
            string.IsNullOrEmpty(logical) ? logical : char.ToUpper(logical[0]) + logical[1..];

        // Mapper voor CDM
        private static string MapToCdmType(string dvType) => dvType switch
        {
            "Uniqueidentifier" => "guid",
            "Lookup" => "guid",
            "String" => "string",
            "Memo" => "string",
            "Integer" => "integer",
            "BigInt" => "integer",
            "Double" => "double",
            "Decimal" => "decimal",
            "Money" => "decimal",
            "Boolean" => "boolean",
            "DateTime" => "dateTime",
            _ => "string"
        };

        private static void WriteRootManifest(string outputDir, string schemaVersion, IEnumerable<string> domainNames)
        {
            var subManifests = new JArray();
            foreach (var name in domainNames)
            {
                subManifests.Add(new JObject
                {
                    ["manifestName"] = name,
                    ["definition"] = $"{name}/{name}.manifest.cdm.json"
                });
            }

            var root = new JObject
            {
                ["manifestName"] = RootManifestName,
                ["jsonSchemaSemanticVersion"] = schemaVersion,
                ["imports"] = new JArray
                {
                    new JObject { ["corpusPath"] = "cdm:/core/cdsConcepts.cdm.json" },
                    new JObject { ["corpusPath"] = "cdm:/foundations.cdm.json" }
                },
                ["subManifests"] = subManifests
            };

            File.WriteAllText(
                Path.Combine(outputDir, $"{RootManifestName}.manifest.cdm.json"),
                JsonConvert.SerializeObject(root, Formatting.Indented));
        }

        private static void WriteDomainManifest(string domainDir, string domainName, string schemaVersion, bool writeStandard, bool writeCustom)
        {
            var subManifests = new JArray();

            if (writeStandard)
            {
                subManifests.Add(new JObject
                {
                    ["manifestName"] = "Standard",
                    ["definition"] = $"Standard/Standard.manifest.cdm.json"
                });
            }
            if (writeCustom)
            {
                subManifests.Add(new JObject
                {
                    ["manifestName"] = "Custom",
                    ["definition"] = $"Custom/Custom.manifest.cdm.json"
                });
            }

            var dom = new JObject
            {
                ["manifestName"] = domainName,
                ["jsonSchemaSemanticVersion"] = schemaVersion,
                ["imports"] = new JArray
                {
                    new JObject { ["corpusPath"] = "cdm:/core/cdsConcepts.cdm.json" },
                    new JObject { ["corpusPath"] = "cdm:/foundations.cdm.json" }
                },
                ["subManifests"] = subManifests
            };

            Directory.CreateDirectory(domainDir);
            File.WriteAllText(
                Path.Combine(domainDir, $"{domainName}.manifest.cdm.json"),
                JsonConvert.SerializeObject(dom, Formatting.Indented));
        }

        private static void WriteLeafManifest(string leafDir, string leafName, string schemaVersion,
            IEnumerable<(string EntityName, string EntityDocPath)> entities,
            IEnumerable<(string FromEntityPath, string FromAttr, string ToEntityPath, string ToAttr)> relationships)
        {
            var entityArray = new JArray();
            foreach (var (eName, docPath) in entities)
            {
                entityArray.Add(new JObject
                {
                    ["type"] = "LocalEntity",
                    ["entityName"] = eName,
                    ["entityPath"] = $"{docPath}/{eName}"
                });
            }

            var relArray = new JArray();
            foreach (var (fromPath, fromAttr, toPath, toAttr) in relationships)
            {
                relArray.Add(new JObject
                {
                    ["fromEntity"] = fromPath,
                    ["fromEntityAttribute"] = fromAttr,
                    ["toEntity"] = toPath,
                    ["toEntityAttribute"] = toAttr
                });
            }

            var leaf = new JObject
            {
                ["manifestName"] = leafName,
                ["jsonSchemaSemanticVersion"] = schemaVersion,
                ["imports"] = new JArray
                {
                    new JObject { ["corpusPath"] = "cdm:/core/cdsConcepts.cdm.json" },
                    new JObject { ["corpusPath"] = "cdm:/foundations.cdm.json" }
                },
                ["entities"] = entityArray,
                ["relationships"] = relArray
            };

            Directory.CreateDirectory(leafDir);
            File.WriteAllText(
                Path.Combine(leafDir, $"{leafName}.manifest.cdm.json"),
                JsonConvert.SerializeObject(leaf, Formatting.Indented));
        }

        private static string PickDomainFor(string logicalName)
        {
            foreach (var kv in SubManifestEntitySets)
                if (kv.Value.Contains(logicalName))
                    return kv.Key;
            return "GENERAL";
        }

        private static bool IsCustom(string logicalName)
        {
            foreach (var p in KnownCustomPrefixes)
                if (logicalName.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }
    }

    // ===== Modelklassen voor solution-export =====
    public class SolutionExport
    {
        public string SolutionName { get; set; } = "";
        public List<SolutionEntity> Entities { get; set; } = new();
        public List<SolutionRelationship> Relationships { get; set; } = new();
    }

    public class SolutionEntity
    {
        public string LogicalName { get; set; } = "";
        public string PrimaryId { get; set; } = "";
        public List<SolutionAttribute> Attributes { get; set; } = new();
    }

    public class SolutionAttribute
    {
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
    }

    public class SolutionRelationship
    {
        public string Name { get; set; } = "";
        public string FromEntity { get; set; } = "";
        public string FromAttribute { get; set; } = "";
        public string ToEntity { get; set; } = "";
        public string ToAttribute { get; set; } = "";
    }
}
