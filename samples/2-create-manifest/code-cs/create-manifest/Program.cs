// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace create_manifest
{
    using Microsoft.PowerPlatform.Dataverse.Client;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Threading.Tasks;

    class Program
    {
        private static readonly string ProjectRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

        private static readonly string BaseOutputRoot = Path.Combine(ProjectRoot, "cdm-out");
        private static readonly string SchemaFilesSource = Path.Combine(ProjectRoot, "needed-files");
        private static readonly string DomainMappingFile = Path.Combine(SchemaFilesSource, "domain-mapping.csv");
        private const string SchemaVersion = "1.1.0";

        // Leveranciers (oude werkwijze, behouden)
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

        static async Task Main(string[] args)
        {
            Console.WriteLine("Mappencontrole...");
            Console.WriteLine($"ProjectRoot: {ProjectRoot}");
            Console.WriteLine($"BaseOutputRoot: {BaseOutputRoot}");
            Console.WriteLine($"SchemaFilesSource: {SchemaFilesSource}");
            Console.WriteLine();

            // Menu
            Console.WriteLine("Stap 1: Ophalen metadata uit Dataverse en wegschrijven (gefilterd)");
            Console.WriteLine("Kies wat je wil exporteren:");
            Console.WriteLine("  1) Alle entiteiten (met domain-mapping)");
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
            bool useDomainMapping = false;

            if (choice == 1)
            {
                prefix = null; // alles
                suffix = "all";
                useDomainMapping = true;
            }
            else
            {
                int idx = choice - 2;
                if (idx < 0 || idx >= Suppliers.Count)
                {
                    Console.WriteLine("Onbekende keuze — standaard: Alle entiteiten.");
                    prefix = null;
                    suffix = "all";
                    useDomainMapping = true;
                }
                else
                {
                    prefix = Suppliers[idx].Prefix;
                    suffix = prefix.ToLowerInvariant();
                }
            }

            string outDir = Path.Combine(BaseOutputRoot, suffix);
            EnsureEmptyDirectory(outDir);

            CopySchemaFilesTo(outDir);

            string exportPath = Path.Combine(outDir, $"entities-{suffix}.json");

            string connectionString = PromptConnectionStringIfEmpty("");
            bool ok = await ExportDataverseMetadataAsync(connectionString, prefix, exportPath);
            if (!ok)
            {
                Console.WriteLine("Export mislukt of geen entiteiten gevonden. Stoppen.");
                return;
            }

            Console.WriteLine("Stap 2: Manifest maken vanuit gefilterde export");
            if (useDomainMapping && File.Exists(DomainMappingFile))
            {
                var mapping = LoadDomainMapping(DomainMappingFile);
                GenerateCdmWithDomains(exportPath, outDir, SchemaVersion, mapping);
            }
            else
            {
                GenerateCdmFromSolutionExport(exportPath, outDir, SchemaVersion);
            }

            Console.WriteLine();
            Console.WriteLine($"Klaar. Bestanden in: {outDir}");
            Console.WriteLine();
        }

        // Zorgt dat een map leeg is
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

            foreach (var em in response.EntityMetadata)
            {
                foreach (var rel in em.OneToManyRelationships ?? Array.Empty<Microsoft.Xrm.Sdk.Metadata.OneToManyRelationshipMetadata>())
                {
                    if (!IncludeByPrefix(prefixFilter, rel.ReferencingEntity ?? "")) continue;
                    solutionExport.Relationships.Add(new SolutionRelationship
                    {
                        Name = rel.SchemaName ?? "",
                        FromEntity = rel.ReferencingEntity ?? "",
                        FromAttribute = rel.ReferencingAttribute ?? "",
                        ToEntity = rel.ReferencedEntity ?? "",
                        ToAttribute = rel.ReferencedAttribute ?? ""
                    });
                }
            }

            Console.WriteLine($"Gefilterd op prefix: {(prefixFilter ?? "<all>")}");
            Console.WriteLine($"Totaal entiteiten: {total} | Geselecteerd: {matched}");
            Console.WriteLine($"Relaties verzameld: {solutionExport.Relationships.Count}");
            Console.WriteLine();

            if (solutionExport.Entities.Count == 0)
            {
                Console.WriteLine("Geen entiteiten gevonden voor de gekozen filter.");
                return false;
            }

            string json = JsonConvert.SerializeObject(solutionExport, Formatting.Indented);
            File.WriteAllText(exportPath, json);
            Console.WriteLine($"Metadata geëxporteerd naar {exportPath}");
            return true;
        }

        // ====== Domain Mapping Loader ======
        private static Dictionary<string, (string Domain, string Category)> LoadDomainMapping(string path)
        {
            var result = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in File.ReadAllLines(path).Skip(1))
            {
                var parts = line.Split(',');
                if (parts.Length < 3) continue;
                string logical = parts[0].Trim();
                string domain = parts[1].Trim();
                string category = parts[2].Trim();
                result[logical] = (domain, category);
            }
            return result;
        }

        // ====== Generate methods ======
        private static void GenerateCdmWithDomains(string inputPath, string outputDir, string schemaVersion, Dictionary<string, (string Domain, string Category)> mapping)
        {
            // Maak root manifest LB365
            string lb365Dir = Path.Combine(outputDir, "LB365");
            EnsureEmptyDirectory(lb365Dir);

            var json = JObject.Parse(File.ReadAllText(inputPath));
            var entities = (JArray)json["Entities"]!;

            // Groepeer per domein+category
            var grouped = new Dictionary<(string Domain, string Category), List<JToken>>();
            foreach (var entity in entities)
            {
                string logical = entity["LogicalName"]!.ToString();
                if (mapping.TryGetValue(logical, out var map))
                {
                    grouped.TryAdd(map, new List<JToken>());
                    grouped[map].Add(entity);
                }
                else
                {
                    grouped.TryAdd(("GENERAL", "Custom"), new List<JToken>());
                    grouped[("GENERAL", "Custom")].Add(entity);
                }
            }

            // Maak submappen
            foreach (var kvp in grouped)
            {
                string domain = kvp.Key.Domain;
                string category = kvp.Key.Category;
                string domainDir = Path.Combine(lb365Dir, domain, category);
                Directory.CreateDirectory(domainDir);

                foreach (var entity in kvp.Value)
                {
                    string logical = entity["LogicalName"]!.ToString();
                    string entityName = ToPascal(logical);
                    string entityFile = Path.Combine(domainDir, $"{entityName}.cdm.json");

                    File.WriteAllText(entityFile, JsonConvert.SerializeObject(new
                    {
                        $schema = "cdm:/schema.cdm.json",
                        jsonSchemaSemanticVersion = schemaVersion,
                        imports = new[] { new { corpusPath = "cdm:/foundations.cdm.json" } },
                        definitions = new[] {
                            new {
                                entityName = entityName,
                                extendsEntity = new { entityReference = "CdmEntity" },
                                hasAttributes = ((JArray)entity["Attributes"]!).Select(a => new {
                                    name = a["Name"]!.ToString(),
                                    dataType = new { dataTypeReference = MapToCdmType(a["Type"]?.ToString() ?? "String") }
                                }).ToArray()
                            }
                        }
                    }, Formatting.Indented));
                }
            }

            Console.WriteLine("Submanifesten gegenereerd per domain-mapping.");
        }

        private static void GenerateCdmFromSolutionExport(string inputPath, string outputDir, string schemaVersion)
        {
            // oude werkwijze (1 manifest, 1 map)
            var json = JObject.Parse(File.ReadAllText(inputPath));
            var entities = (JArray)json["Entities"]!;
            foreach (var entityNode in entities)
            {
                string logicalName = entityNode["LogicalName"]!.ToString();
                string entityName = ToPascal(logicalName);
                string entityFile = Path.Combine(outputDir, $"{entityName}.cdm.json");
                File.WriteAllText(entityFile, JsonConvert.SerializeObject(new
                {
                    $schema = "cdm:/schema.cdm.json",
                    jsonSchemaSemanticVersion = schemaVersion,
                    imports = new[] { new { corpusPath = "cdm:/foundations.cdm.json" } },
                    definitions = new[] {
                        new {
                            entityName = entityName,
                            extendsEntity = new { entityReference = "CdmEntity" },
                            hasAttributes = ((JArray)entityNode["Attributes"]!).Select(a => new {
                                name = a["Name"]!.ToString(),
                                dataType = new { dataTypeReference = MapToCdmType(a["Type"]?.ToString() ?? "String") }
                            }).ToArray()
                        }
                    }
                }, Formatting.Indented));
            }
        }

        // ===== Helpers =====

        private static bool IncludeByPrefix(string? prefix, string logicalName)
            => string.IsNullOrWhiteSpace(prefix)
               || logicalName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
               || logicalName.StartsWith(prefix + "_", StringComparison.OrdinalIgnoreCase);

        private static void CopySchemaFilesTo(string destRoot)
        {
            string srcFoundations = Path.Combine(SchemaFilesSource, "foundations.cdm.json");
            string dstFoundations = Path.Combine(destRoot, "foundations.cdm.json");
            if (File.Exists(srcFoundations)) File.Copy(srcFoundations, dstFoundations, true);

            string srcCore = Path.Combine(SchemaFilesSource, "core", "cdsConcepts.cdm.json");
            string dstCoreDir = Path.Combine(destRoot, "core");
            Directory.CreateDirectory(dstCoreDir);
            string dstCore = Path.Combine(dstCoreDir, "cdsConcepts.cdm.json");
            if (File.Exists(srcCore)) File.Copy(srcCore, dstCore, true);
        }

        private static string ToPascal(string logical)
            => string.IsNullOrEmpty(logical) ? logical : char.ToUpper(logical[0]) + logical[1..];

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
