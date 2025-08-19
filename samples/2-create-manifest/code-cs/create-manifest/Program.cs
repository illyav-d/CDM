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
        //Padbestanden en config todo: van hardcoded naar /.../... paths
        private const string BaseOutputRoot = @"C:\Users\IllyaVerheyden\Desktop\CDM\samples\2-create-manifest\code-cs\cdm-out";
        private const string SchemaFilesSource = @"C:\Users\IllyaVerheyden\Desktop\CDM\samples\2-create-manifest\code-cs\needed-files";
        private const string SchemaVersion = "1.1.0";
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
            // --- Stap 1: Kiezen wat we exporteren 
            Console.WriteLine("Stap 1: Ophalen metadata uit Dataverse en wegschrijven (gefilterd)");
            Console.WriteLine("Kies wat je wil exporteren:");
            Console.WriteLine("  1) Alle entiteiten");
            for (int i = 0; i < Suppliers.Count; i++)
                Console.WriteLine($"  {i + 2}) Prefix: {Suppliers[i].Prefix} ({Suppliers[i].Label})");
            Console.Write("Maak je keuze (getal): ");

            var key = Console.ReadKey(); Console.WriteLine();
            int choice = char.IsDigit(key.KeyChar) ? (key.KeyChar - '0') : 1;

            string? prefix = null;
            string suffix;
            if (choice == 1)
            {
                prefix = null;      // alles
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

            // Copy foundations + cdsConcepts naar output-submap
            CopySchemaFilesTo(outDir);

            // Bestandsnaam voor gefilterde export
            string exportPath = Path.Combine(outDir, $"entities-{suffix}.json");

            // --- Stap 1: Exporteren met filter (prefix of alles) ---
            string connectionString = "";
            bool ok = await ExportDataverseMetadataAsync(connectionString, prefix, exportPath);
            if (!ok)
            {
                Console.WriteLine("Export mislukt of geen entiteiten gevonden. Stoppen.");
                return;
            }

            // --- Stap 2: CDM genereren op basis van die gefilterde export ---
            Console.WriteLine("Stap 2: Manifest maken vanuit gefilterde export");
            GenerateCdmFromSolutionExport(exportPath, outDir, SchemaVersion);

            Console.WriteLine($"Klaar. Bestanden in: {outDir}");
        }

        
        private static void EnsureEmptyDirectory(string path)
        {
            if (Directory.Exists(path))
            {
                Console.WriteLine($"Map gevonden: {path} — inhoud wordt opgeruimd...");
                Directory.Delete(path, recursive: true);
            }
            Directory.CreateDirectory(path);
            Console.WriteLine($"Nieuwe map aangemaakt: {path}");
        }

        /// Exporteert Dataverse metadata en filtert meteen op prefix (of alles).
        private static async Task<bool> ExportDataverseMetadataAsync(string connectionString, string? prefixFilter, string exportPath)
        {
            var serviceClient = new ServiceClient(connectionString);
            if (!serviceClient.IsReady)
            {
                Console.WriteLine("Verbinding met Dataverse mislukt.");
                return false;
            }
            Console.WriteLine("Verbinding met Dataverse succesvol.");

            var solutionExport = new SolutionExport
            {
                SolutionName = prefixFilter is null ? "All" : prefixFilter,
                Entities = new List<SolutionEntity>()
            };

            var request = new Microsoft.Xrm.Sdk.Messages.RetrieveAllEntitiesRequest()
            {
                EntityFilters = Microsoft.Xrm.Sdk.Metadata.EntityFilters.Entity | Microsoft.Xrm.Sdk.Metadata.EntityFilters.Attributes,
                RetrieveAsIfPublished = true
            };
            var response = (Microsoft.Xrm.Sdk.Messages.RetrieveAllEntitiesResponse)serviceClient.Execute(request);

            int total = 0, matched = 0;

            foreach (var entityMetadata in response.EntityMetadata)
            {
                total++;

                string ln = entityMetadata.LogicalName ?? string.Empty;

                bool include = true;
                if (!string.IsNullOrWhiteSpace(prefixFilter))
                {
                    // Robuuste match: zowel "nrq" als "nrq_"
                    include =
                        ln.StartsWith(prefixFilter, StringComparison.OrdinalIgnoreCase) ||
                        ln.StartsWith(prefixFilter + "_", StringComparison.OrdinalIgnoreCase);
                }

                if (!include) continue;

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
                    Attributes = attributes
                });
            }

            // log aantallen + een paar namen
            Console.WriteLine($"Gefilterd op prefix: {(prefixFilter ?? "<all>")}");
            Console.WriteLine($"Totaal entiteiten: {total} | Geselecteerd: {matched}");
            if (matched > 0)
            {
                int show = Math.Min(10, solutionExport.Entities.Count);
                Console.WriteLine("Voorbeeld(en):");
                for (int i = 0; i < show; i++)
                    Console.WriteLine($"  - {solutionExport.Entities[i].LogicalName}");
            }

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

        /// Leest de (gefilterde) JSON en genereert CDM: *.cdm.json, partitions en manifest.
        private static void GenerateCdmFromSolutionExport(string inputPath, string outputDir, string schemaVersion)
        {
            var json = JObject.Parse(File.ReadAllText(inputPath));
            var entities = (JArray)json["Entities"]!;

            var manifest = new JObject
            {
                ["manifestName"] = "default",
                ["jsonSchemaSemanticVersion"] = schemaVersion,
                ["entities"] = new JArray(),
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

                // === Attributes ===
                var attrs = new JArray();
                var csvHeader = new StringBuilder();

                foreach (var attr in (JArray)entityNode["Attributes"]!)
                {
                    string name = attr["Name"]!.ToString();
                    string type = attr["Type"]?.ToString() ?? "String";
                    string cdmType = MapToCdmType(type);

                    // CDM: dataType als object met dataTypeReference
                    attrs.Add(new JObject
                    {
                        ["name"] = name,
                        ["dataType"] = new JObject { ["dataTypeReference"] = cdmType }
                    });

                    if (csvHeader.Length > 0) csvHeader.Append(',');
                    csvHeader.Append(name);
                }

                // === Entity JSON ===
                var entityDoc = new JObject
                {
                    ["$schema"] = "cdm:/schema.cdm.json",
                    ["jsonSchemaSemanticVersion"] = schemaVersion,
                    ["imports"] = new JArray(new JObject { ["corpusPath"] = "cdm:/foundations.cdm.json" }),
                    ["definitions"] = new JArray(new JObject
                    {
                        ["entityName"] = entityName,
                        ["extendsEntity"] = new JObject { ["entityReference"] = "CdmEntity" },
                        ["hasAttributes"] = attrs
                    })
                };

                // Schrijf <Entity>.cdm.json
                string entityFile = Path.Combine(outputDir, $"{entityName}.cdm.json");
                File.WriteAllText(entityFile, JsonConvert.SerializeObject(entityDoc, Formatting.Indented));

                // === Partition CSV + folder ===
                string entityFolder = Path.Combine(outputDir, entityName);
                Directory.CreateDirectory(entityFolder);
                string csvPath = Path.Combine(entityFolder, "partition-data.csv");
                File.WriteAllText(csvPath, csvHeader.ToString() + Environment.NewLine);

                // === Manifest entry ===
                var partition = new JObject
                {
                    ["name"] = $"{entityName}-data-description",
                    ["location"] = $"{entityName}/partition-data.csv",
                    ["exhibitsTraits"] = new JArray(new JObject
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

            // Manifest opslaan
            string manifestFile = Path.Combine(outputDir, "default.manifest.cdm.json");
            File.WriteAllText(manifestFile, JsonConvert.SerializeObject(manifest, Formatting.Indented));
        }

        // ===== Methodes =====

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
                Console.WriteLine("⚠️ foundations.cdm.json niet gevonden in needed-files");
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
                Console.WriteLine("⚠️ core/cdsConcepts.cdm.json niet gevonden in needed-files");
            }
        }

        private static string ToPascal(string logical) =>
            string.IsNullOrEmpty(logical) ? logical : char.ToUpper(logical[0]) + logical[1..];

        //Mapper voor CDM
        private static string MapToCdmType(string dvType) => dvType switch
        {
            "Uniqueidentifier" => "string",
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
    }

    public class SolutionEntity
    {
        public string LogicalName { get; set; } = "";
        public List<SolutionAttribute> Attributes { get; set; } = new();
    }

    public class SolutionAttribute
    {
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
    }
}
