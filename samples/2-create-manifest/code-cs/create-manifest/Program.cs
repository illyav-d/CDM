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
    using System.Text;
    using System.Threading.Tasks;

    class Program
    {
        // Padbestanden en config (TODO: van hardcoded naar config/args indien gewenst)

        private static readonly string ProjectRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..",".."));

        //Mappen
        private static readonly string BaseOutputRoot = Path.Combine(ProjectRoot, "cdm-out");
        private static readonly string SchemaFilesSource = Path.Combine(ProjectRoot, "needed-files");
        private const string SchemaVersion = "1.1.0";


        // Leveranciers (prefix, label)
        static readonly List<(string Prefix, string Label)> Suppliers = new()
        {
            ("nrq",  "Norriq"),
            ("svc",  "Savaco"),
            ("ccp",  "Valantic"),
            ("sgw",  "Sint-Gillis-Waas"),
            ("ccsp", "BeginPrefix"),
            ("qbx",  "Qubix")
        };

        static async Task Main(string[] args)
        {
            Console.WriteLine("Mappencontrole...");
            Console.WriteLine($"ProjectRoot: {ProjectRoot}");
            Console.WriteLine($"BaseOutputRoot: {BaseOutputRoot}");
            Console.WriteLine($"SchemaFilesSource: {SchemaFilesSource}");
            Console.WriteLine();

            // --- Stap 1: Keuze menu ---
            Console.WriteLine("Stap 1: Ophalen metadata uit Dataverse en wegschrijven (gefilterd)");
            Console.WriteLine("Kies wat je wil exporteren:");
            Console.WriteLine("  1) Alle entiteiten");
            for (int i = 0; i < Suppliers.Count; i++)
                Console.WriteLine($"  {i + 2}) Prefix: {Suppliers[i].Prefix} ({Suppliers[i].Label})");
            Console.Write("Maak je keuze (getal): ");

            var key = Console.ReadKey(); Console.WriteLine();
            int choice = char.IsDigit(key.KeyChar) ? (key.KeyChar - '0') : 1;
            Console.WriteLine();

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

            // --- Stap 2: CDM genereren op basis van die gefilterde export ---
            Console.WriteLine("Stap 2: Manifest maken vanuit gefilterde export");
            GenerateCdmFromSolutionExport(exportPath, outDir, SchemaVersion);
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

            // --- Entities + attributes ---
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

            // --- Relationships (1:N en N:1) ---
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

        /// Leest de (gefilterde) JSON en genereert CDM: *.cdm.json, partitions en manifest.
        /// Schrijft ook manifest.relationships zodat SchemaViz relaties tekent.
        private static void GenerateCdmFromSolutionExport(string inputPath, string outputDir, string schemaVersion)
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

            var fkLookup = new Dictionary<(string entity, string attr), (string toEntity, string toAttr)>(
                new TupleStringIgnoreCaseComparer()
            );
            if (rels != null)
            {
                foreach (var r in rels)
                {
                    var fe = r["FromEntity"]?.ToString() ?? "";
                    var fa = r["FromAttribute"]?.ToString() ?? "";
                    var te = r["ToEntity"]?.ToString() ?? "";
                    var ta = r["ToAttribute"]?.ToString() ?? "";
                    if (!string.IsNullOrWhiteSpace(fe) && !string.IsNullOrWhiteSpace(fa) &&
                        !string.IsNullOrWhiteSpace(te) && !string.IsNullOrWhiteSpace(ta))
                    {
                        fkLookup[(fe, fa)] = (te, ta);
                    }
                }
            }

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

                    // FK-trait (sample-vorm met entityGroupSet/constantValues)
                    if (fkLookup.TryGetValue((logicalName, name), out var target))
                    {
                        string toEntityPascal = ToPascal(target.toEntity);
                        string targetPath = $"{toEntityPascal}.cdm.json/{toEntityPascal}";
                        string targetPk = string.IsNullOrWhiteSpace(target.toAttr) ? "id" : target.toAttr;

                        attrTraits ??= new JArray();
                        attrTraits.Add(
                            new JObject
                            {
                                ["traitReference"] = "is.linkedEntity.identifier",
                                ["arguments"] = new JArray(
                                    new JObject
                                    {
                                        ["entityReference"] = new JObject
                                        {
                                            ["entityShape"] = "entityGroupSet",
                                            ["constantValues"] = new JArray(
                                                new JArray(targetPath, targetPk)
                                            )
                                        }
                                    }
                                )
                            }
                        );
                    }

                    // PK-trait op attribuut
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
                    {
                        // BELANGRIJK: appliedTraits ipv exhibitsTraits
                        attrObj["appliedTraits"] = attrTraits;
                    }

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

                var partition = new JObject
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
                };

                ((JArray)manifest["entities"]!).Add(new JObject
                {
                    ["type"] = "LocalEntity",
                    ["entityName"] = entityName,
                    ["entityPath"] = $"{entityName}.cdm.json/{entityName}",
                    ["dataPartitions"] = new JArray(partition)
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
                    {
                        continue;
                    }

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

    class TupleStringIgnoreCaseComparer : IEqualityComparer<(string entity, string attr)>
    {
        public bool Equals((string entity, string attr) x, (string entity, string attr) y)
        {
            return string.Equals(x.entity, y.entity, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.attr, y.attr, StringComparison.OrdinalIgnoreCase);
        }

        public int GetHashCode((string entity, string attr) obj)
        {
            int h1 = StringComparer.OrdinalIgnoreCase.GetHashCode(obj.entity ?? "");
            int h2 = StringComparer.OrdinalIgnoreCase.GetHashCode(obj.attr ?? "");
            return HashCode.Combine(h1, h2);
        }
    }
}
