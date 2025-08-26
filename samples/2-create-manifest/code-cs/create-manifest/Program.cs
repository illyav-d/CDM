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
        private static readonly string ProjectRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

        private static readonly string BaseOutputRoot = Path.Combine(ProjectRoot, "cdm-out");
        private static readonly string SchemaFilesSource = Path.Combine(ProjectRoot, "needed-files");
        private static readonly string DomainMappingFile = Path.Combine(SchemaFilesSource, "domain-mapping.csv");
        private const string SchemaVersion = "1.1.0";

        private static readonly string[] AllDomains =
        {
            "GENERAL",
            "MELDINGEN",
            "KLACHTEN",
            "POSTREGISTRATIE",
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

        static void Main(string[] args)
        {
            Console.WriteLine("Mappencontrole...");
            Console.WriteLine($"ProjectRoot: {ProjectRoot}");
            Console.WriteLine($"BaseOutputRoot: {BaseOutputRoot}");
            Console.WriteLine($"SchemaFilesSource: {SchemaFilesSource}");
            Console.WriteLine();

            Console.WriteLine("Stap 1: Ophalen metadata uit Dataverse en wegschrijven (gefilterd)");
            Console.WriteLine("Kies wat je wil exporteren:");
            Console.WriteLine("  1) Alle entiteiten (met domain-mapping + manifests)");
            for (int i = 0; i < Suppliers.Count; i++)
                Console.WriteLine($"  {i + 2}) Prefix: {Suppliers[i].Prefix} ({Suppliers[i].Label})");
            Console.Write("Maak je keuze (getal): ");
            string input = Console.ReadLine();
            if (!int.TryParse(input, out int choice))
            {
                Console.WriteLine("Ongeldige invoer — standaard: Alle entiteiten.");
                choice = 1;
            }

            string prefix = null;
            string suffix;
            bool useDomainMapping = false;

            if (choice == 1)
            {
                prefix = null;
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

            if (useDomainMapping)
            {
                var lb365Root = Path.Combine(outDir, "LB365");
                EnsureEmptyDirectory(lb365Root);
                PrecreateDomainFolders(lb365Root);
            }

            string exportPath = Path.Combine(outDir, $"entities-{suffix}.json");

            string connectionString = PromptConnectionStringIfEmpty("");
            bool ok = ExportDataverseMetadata(connectionString, prefix, exportPath);
            if (!ok)
            {
                Console.WriteLine("Export mislukt of geen entiteiten gevonden. Stoppen.");
                return;
            }

            Console.WriteLine("Stap 2: Manifest/entiteiten genereren vanuit export");
            if (useDomainMapping && File.Exists(DomainMappingFile))
            {
                var mapping = LoadDomainMapping(DomainMappingFile);
                GenerateCdmWithDomainsAndManifests(exportPath, outDir, SchemaVersion, mapping);
            }
            else
            {
                GenerateFlatCdmWithManifest(exportPath, outDir, SchemaVersion);
            }

            Console.WriteLine();
            Console.WriteLine($"Klaar. Bestanden in: {outDir}");
            Console.WriteLine();
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

        private static bool ExportDataverseMetadata(string connectionString, string prefixFilter, string exportPath)
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
                EntityFilters = Microsoft.Xrm.Sdk.Metadata.EntityFilters.Entity
                              | Microsoft.Xrm.Sdk.Metadata.EntityFilters.Attributes
                              | Microsoft.Xrm.Sdk.Metadata.EntityFilters.Relationships,
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
                    string referencingEntity = rel.ReferencingEntity ?? "";
                    if (!IncludeByPrefix(prefixFilter, referencingEntity)) continue;

                    solutionExport.Relationships.Add(new SolutionRelationship
                    {
                        Name = rel.SchemaName ?? "",
                        FromEntity = referencingEntity,
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

        private static Dictionary<string, (string Domain, string Category)> LoadDomainMapping(string path)
        {
            var result = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
            var lines = File.ReadAllLines(path, Encoding.UTF8).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
            if (lines.Count <= 1) return result;

            foreach (var line in lines.Skip(1))
            {
                var parts = SplitCsvLine(line);
                if (parts.Length < 3) continue;
                string logical = parts[0].Trim();
                string domain = parts[1].Trim();
                string category = parts[2].Trim();
                if (!string.IsNullOrEmpty(logical))
                    result[logical] = (domain, category);
            }
            return result;
        }

        private static string[] SplitCsvLine(string line)
        {
            var list = new List<string>();
            var sb = new StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (inQuotes)
                {
                    if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        sb.Append('"'); i++;
                    }
                    else if (c == '"')
                    {
                        inQuotes = false;
                    }
                    else
                    {
                        sb.Append(c);
                    }
                }
                else
                {
                    if (c == ',')
                    {
                        list.Add(sb.ToString());
                        sb.Clear();
                    }
                    else if (c == '"')
                    {
                        inQuotes = true;
                    }
                    else
                    {
                        sb.Append(c);
                    }
                }
            }
            list.Add(sb.ToString());
            return list.ToArray();
        }

        // =========================
        // MANIFEST GENERATION (ALL)
        // =========================

        private static void GenerateCdmWithDomainsAndManifests(
            string inputPath,
            string outDir,
            string schemaVersion,
            Dictionary<string, (string Domain, string Category)> mapping)
        {
            string lb365Dir = Path.Combine(outDir, "LB365");
            var json = JObject.Parse(File.ReadAllText(inputPath));
            var entities = (JArray)json["Entities"]!;
            var rels = (JArray)json["Relationships"] ?? new JArray();

            var grouped = new Dictionary<(string Domain, string Category), List<JToken>>();
            foreach (var entity in entities)
            {
                string logical = entity["LogicalName"]!.ToString();
                (string Domain, string Category) place;
                if (!mapping.TryGetValue(logical, out place))
                {
                    place = ("GENERAL", "Custom");
                }
                if (!AllDomains.Contains(place.Domain, StringComparer.OrdinalIgnoreCase))
                {
                    place = ("GENERAL", place.Category);
                }
                grouped.TryAdd(place, new List<JToken>());
                grouped[place].Add(entity);
            }

            var domainManifests = new List<(string Domain, string ManifestFile)>();

            foreach (var domain in AllDomains)
            {
                string domainDir = Path.Combine(lb365Dir, domain);
                string stdDir = Path.Combine(domainDir, "Standard");
                string cusDir = Path.Combine(domainDir, "Custom");

                Directory.CreateDirectory(stdDir);
                Directory.CreateDirectory(cusDir);

                CopySchemaFilesTo(stdDir);
                CopySchemaFilesTo(cusDir);

                var stdEntities = grouped.TryGetValue((domain, "Standard"), out var s) ? s : new List<JToken>();
                var cusEntities = grouped.TryGetValue((domain, "Custom"), out var c) ? c : new List<JToken>();

                string stdManifest = Path.Combine(stdDir, "Standard.manifest.cdm.json");
                string cusManifest = Path.Combine(cusDir, "Custom.manifest.cdm.json");

                WriteLeafManifestWithEntities(stdDir, stdEntities, schemaVersion, stdManifest, "Standard", rels);
                WriteLeafManifestWithEntities(cusDir, cusEntities, schemaVersion, cusManifest, "Custom", rels);

                string domainManifestPath = Path.Combine(domainDir, $"{domain}.manifest.cdm.json");
                WriteDomainManifest(domainManifestPath, schemaVersion, stdDir, cusDir, stdEntities, cusEntities, rels);

                domainManifests.Add((domain, domainManifestPath));
            }

            string lbRootManifest = Path.Combine(lb365Dir, "LB365.manifest.cdm.json");
            WriteRootManifest(lbRootManifest, schemaVersion, domainManifests);
        }

        private static void WriteLeafManifestWithEntities(
            string folder,
            List<JToken> entities,
            string schemaVersion,
            string manifestPath,
            string leafName,
            JArray allRels)
        {
            var manifest = new JObject
            {
                ["manifestName"] = leafName,
                ["jsonSchemaSemanticVersion"] = schemaVersion,
                ["entities"] = new JArray(),
                ["relationships"] = new JArray(),
                ["imports"] = new JArray
                {
                    new JObject { ["corpusPath"] = "cdm:/core/cdsConcepts.cdm.json" },
                    new JObject { ["corpusPath"] = "cdm:/foundations.cdm.json" }
                }
            };

            var inLeaf = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var entity in entities)
            {
                string logical = entity["LogicalName"]!.ToString();
                inLeaf.Add(logical);
                string entityName = ToPascal(logical);
                string primaryId = (entity["PrimaryId"]?.ToString() ?? "").Trim();

                var attrs = new JArray();
                var csvHeader = new StringBuilder();
                foreach (var a in (JArray)entity["Attributes"]!)
                {
                    string name = a["Name"]!.ToString();
                    string type = a["Type"]?.ToString() ?? "String";

                    attrs.Add(new JObject
                    {
                        ["name"] = name,
                        ["dataType"] = new JObject { ["dataTypeReference"] = MapToCdmType(type) }
                    });

                    if (csvHeader.Length > 0) csvHeader.Append(',');
                    csvHeader.Append(name);
                }

                var entityDef = new JObject
                {
                    ["entityName"] = entityName,
                    ["extendsEntity"] = new JObject { ["entityReference"] = "CdmEntity" },
                    ["hasAttributes"] = attrs
                };

                if (!string.IsNullOrWhiteSpace(primaryId))
                {
                    entityDef["exhibitsTraits"] = new JArray(
                        new JObject
                        {
                            ["traitReference"] = "is.identifiedBy",
                            ["arguments"] = new JArray(
                                new JObject
                                {
                                    ["name"] = "attribute",
                                    ["value"] = new JObject
                                    {
                                        ["entityName"] = entityName,
                                        ["attributeName"] = primaryId
                                    }
                                }
                            )
                        }
                    );
                }

                var entityDoc = new JObject(
                    new JProperty("$schema", "cdm:/schema.cdm.json"),
                    new JProperty("jsonSchemaSemanticVersion", schemaVersion),
                    new JProperty("imports", new JArray(new JObject { ["corpusPath"] = "cdm:/foundations.cdm.json" })),
                    new JProperty("definitions", new JArray(entityDef))
                );

                string entityFile = Path.Combine(folder, $"{entityName}.cdm.json");
                File.WriteAllText(entityFile, JsonConvert.SerializeObject(entityDoc, Formatting.Indented));

                string entityFolder = Path.Combine(folder, entityName);
                Directory.CreateDirectory(entityFolder);
                File.WriteAllText(Path.Combine(entityFolder, "partition-data.csv"), csvHeader + Environment.NewLine);

                ((JArray)manifest["entities"]!).Add(new JObject
                {
                    ["type"] = "LocalEntity",
                    ["entityName"] = entityName,
                    ["entityPath"] = $"{entityName}.cdm.json/{entityName}",
                    ["dataPartitions"] = new JArray(
                        new JObject
                        {
                            ["name"] = $"{entityName}-data",
                            ["location"] = $"{entityName}/partition-data.csv",
                            ["appliedTraits"] = new JArray(
                                new JObject
                                {
                                    ["traitReference"] = "is.partition.format.CSV",
                                    ["arguments"] = new JArray(
                                        new JObject { ["name"] = "columnHeaders", ["value"] = "true" },
                                        new JObject { ["name"] = "delimiter", ["value"] = "," }
                                    )
                                }
                            )
                        }
                    )
                });
            }

            if (allRels != null && allRels.Count > 0)
            {
                foreach (var r in allRels)
                {
                    string fromLogical = r["FromEntity"]?.ToString() ?? "";
                    string toLogical = r["ToEntity"]?.ToString() ?? "";
                    if (!inLeaf.Contains(fromLogical) || !inLeaf.Contains(toLogical))
                        continue;

                    string fromEntity = ToPascal(fromLogical);
                    string toEntity = ToPascal(toLogical);
                    string fromAttr = r["FromAttribute"]?.ToString() ?? "";
                    string toAttr = r["ToAttribute"]?.ToString() ?? "";
                    if (string.IsNullOrWhiteSpace(fromAttr) || string.IsNullOrWhiteSpace(toAttr))
                        continue;

                    ((JArray)manifest["relationships"]!).Add(new JObject
                    {
                        ["fromEntity"] = $"{fromEntity}.cdm.json/{fromEntity}",
                        ["fromEntityAttribute"] = fromAttr,
                        ["toEntity"] = $"{toEntity}.cdm.json/{toEntity}",
                        ["toEntityAttribute"] = toAttr
                    });
                }
            }

            File.WriteAllText(manifestPath, JsonConvert.SerializeObject(manifest, Formatting.Indented));
        }

        private static void WriteDomainManifest(string manifestPath,string schemaVersion,string stdDir,string cusDir,List<JToken> stdEntities,List<JToken> cusEntities,JArray allRels)
        {
            string stdRel = ToCorpusPath(manifestPath, Path.Combine(stdDir, "Standard.manifest.cdm.json"));
            string cusRel = ToCorpusPath(manifestPath, Path.Combine(cusDir, "Custom.manifest.cdm.json"));

            var manifest = new JObject
            {
                ["manifestName"] = Path.GetFileNameWithoutExtension(manifestPath).Replace(".manifest", ""),
                ["jsonSchemaSemanticVersion"] = schemaVersion,
                ["imports"] = new JArray
                {
                    new JObject { ["corpusPath"] = "cdm:/core/cdsConcepts.cdm.json" },
                    new JObject { ["corpusPath"] = "cdm:/foundations.cdm.json" }
                },
                ["subManifests"] = new JArray
                {
                    new JObject { ["manifestName"] = "Standard", ["definition"] = stdRel },
                    new JObject { ["manifestName"] = "Custom",   ["definition"] = cusRel }
                },
                ["relationships"] = new JArray()
            };

            var stdSet = new HashSet<string>(stdEntities.Select(e => e["LogicalName"]!.ToString()), StringComparer.OrdinalIgnoreCase);
            var cusSet = new HashSet<string>(cusEntities.Select(e => e["LogicalName"]!.ToString()), StringComparer.OrdinalIgnoreCase);
            bool Has(string logical) => stdSet.Contains(logical) || cusSet.Contains(logical);

            string PathFor(string logical)
            {
                string pascal = ToPascal(logical);
                if (stdSet.Contains(logical))
                    return $"Standard/{pascal}.cdm.json/{pascal}";
                if (cusSet.Contains(logical))
                    return $"Custom/{pascal}.cdm.json/{pascal}";
                return $"{pascal}.cdm.json/{pascal}";
            }

            foreach (var r in allRels)
            {
                string fromLogical = r["FromEntity"]?.ToString() ?? "";
                string toLogical = r["ToEntity"]?.ToString() ?? "";
                if (!Has(fromLogical) || !Has(toLogical)) continue;

                string fromAttr = r["FromAttribute"]?.ToString() ?? "";
                string toAttr = r["ToAttribute"]?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(fromAttr) || string.IsNullOrWhiteSpace(toAttr)) continue;

                ((JArray)manifest["relationships"]!).Add(new JObject
                {
                    ["fromEntity"] = PathFor(fromLogical),
                    ["fromEntityAttribute"] = fromAttr,
                    ["toEntity"] = PathFor(toLogical),
                    ["toEntityAttribute"] = toAttr
                });
            }

            File.WriteAllText(manifestPath, JsonConvert.SerializeObject(manifest, Formatting.Indented));
        }

        private static void WriteRootManifest(string manifestPath, string schemaVersion, List<(string Domain, string ManifestFile)> domainManifests)
        {
            var subs = new JArray();
            foreach (var (Domain, ManifestFile) in domainManifests)
            {
                string rel = ToCorpusPath(manifestPath, ManifestFile);
                subs.Add(new JObject { ["manifestName"] = Domain, ["definition"] = rel });
            }

            var manifest = new JObject
            {
                ["manifestName"] = "LB365",
                ["jsonSchemaSemanticVersion"] = schemaVersion,
                ["imports"] = new JArray
                {
                    new JObject { ["corpusPath"] = "cdm:/core/cdsConcepts.cdm.json" },
                    new JObject { ["corpusPath"] = "cdm:/foundations.cdm.json" }
                },
                ["subManifests"] = subs
            };

            File.WriteAllText(manifestPath, JsonConvert.SerializeObject(manifest, Formatting.Indented));
        }


        // =============================
        // Leverancier
        // =============================

        private static void GenerateFlatCdmWithManifest(string inputPath, string outputDir, string schemaVersion)
        {
            var json = JObject.Parse(File.ReadAllText(inputPath));
            var entities = (JArray)json["Entities"]!;
            var rels = (JArray?)json["Relationships"] ?? new JArray();

            // 1) Set met logical names in deze subset
            var inLeaf = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in entities)
                inLeaf.Add(e["LogicalName"]!.ToString());

            // 2) FK lookup: (FromEntity, FromAttribute) -> (ToEntity, ToAttribute)
            var fkLookup = new Dictionary<(string entity, string attr), (string toEntity, string toAttr)>(new TupleStringIgnoreCaseComparer());
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

            // 3) Entities + appliedTraits (PK/FK) + partitions
            foreach (var entityNode in entities)
            {
                string logicalName = entityNode["LogicalName"]!.ToString();
                string entityName = ToPascal(logicalName);
                string primaryId = (entityNode["PrimaryId"]?.ToString() ?? "").Trim();

                var attrs = new JArray();
                var csvHeader = new StringBuilder();

                foreach (var a in (JArray)entityNode["Attributes"]!)
                {
                    string name = a["Name"]!.ToString();
                    string type = a["Type"]?.ToString() ?? "String";
                    var attrObj = new JObject
                    {
                        ["name"] = name,
                        ["dataType"] = new JObject { ["dataTypeReference"] = MapToCdmType(type) }
                    };

                    // PK trait
                    if (!string.IsNullOrWhiteSpace(primaryId) &&
                        string.Equals(name, primaryId, StringComparison.OrdinalIgnoreCase))
                    {
                        attrObj["appliedTraits"] = new JArray(new JObject { ["traitReference"] = "means.identity.entityId" });
                    }

                    // FK trait (kleur + link)
                    if (fkLookup.TryGetValue((logicalName, name), out var target))
                    {
                        string toEntityPascal = ToPascal(target.toEntity);
                        string targetPath = $"{toEntityPascal}.cdm.json/{toEntityPascal}";
                        string targetPk = string.IsNullOrWhiteSpace(target.toAttr) ? "id" : target.toAttr;

                        var traits = (JArray?)attrObj["appliedTraits"] ?? new JArray();
                        traits.Add(
                            new JObject
                            {
                                ["traitReference"] = "is.linkedEntity.identifier",
                                ["arguments"] = new JArray(
                                    new JObject
                                    {
                                        ["entityReference"] = new JObject
                                        {
                                            ["entityShape"] = "entityGroupSet",
                                            ["constantValues"] = new JArray(new JArray(targetPath, targetPk))
                                        }
                                    }
                                )
                            }
                        );
                        attrObj["appliedTraits"] = traits;
                    }

                    attrs.Add(attrObj);

                    if (csvHeader.Length > 0) csvHeader.Append(',');
                    csvHeader.Append(name);
                }

                var entityDoc = new JObject(
                    new JProperty("$schema", "cdm:/schema.cdm.json"),
                    new JProperty("jsonSchemaSemanticVersion", schemaVersion),
                    new JProperty("imports", new JArray(new JObject { ["corpusPath"] = "cdm:/foundations.cdm.json" })),
                    new JProperty("definitions", new JArray(
                        new JObject
                        {
                            ["entityName"] = entityName,
                            ["extendsEntity"] = new JObject { ["entityReference"] = "CdmEntity" },
                            ["hasAttributes"] = attrs
                        }
                    ))
                );

                string entityFile = Path.Combine(outputDir, $"{entityName}.cdm.json");
                File.WriteAllText(entityFile, JsonConvert.SerializeObject(entityDoc, Formatting.Indented));

                string entityFolder = Path.Combine(outputDir, entityName);
                Directory.CreateDirectory(entityFolder);
                File.WriteAllText(Path.Combine(entityFolder, "partition-data.csv"), csvHeader.ToString() + Environment.NewLine);

                ((JArray)manifest["entities"]!).Add(new JObject
                {
                    ["type"] = "LocalEntity",
                    ["entityName"] = entityName,
                    ["entityPath"] = $"{entityName}.cdm.json/{entityName}",
                    ["dataPartitions"] = new JArray(
                        new JObject
                        {
                            ["name"] = $"{entityName}-data",
                            ["location"] = $"{entityName}/partition-data.csv",
                            ["appliedTraits"] = new JArray(
                                new JObject
                                {
                                    ["traitReference"] = "is.partition.format.CSV",
                                    ["arguments"] = new JArray(
                                        new JObject { ["name"] = "columnHeaders", ["value"] = "true" },
                                        new JObject { ["name"] = "delimiter", ["value"] = "," }
                                    )
                                }
                            )
                        }
                    )
                });
            }

            // 4) Alleen relaties tekenen waarvan beide kanten in de subset zitten
            foreach (var r in rels)
            {
                string fromEntityLogical = r["FromEntity"]?.ToString() ?? "";
                string toEntityLogical = r["ToEntity"]?.ToString() ?? "";
                string fromAttribute = r["FromAttribute"]?.ToString() ?? "";
                string toAttribute = r["ToAttribute"]?.ToString() ?? "";

                if (string.IsNullOrWhiteSpace(fromAttribute) || string.IsNullOrWhiteSpace(toAttribute))
                    continue;

                if (!inLeaf.Contains(fromEntityLogical) || !inLeaf.Contains(toEntityLogical))
                    continue;

                string fromEntity = ToPascal(fromEntityLogical);
                string toEntity = ToPascal(toEntityLogical);

                ((JArray)manifest["relationships"]!).Add(new JObject
                {
                    ["fromEntity"] = $"{fromEntity}.cdm.json/{fromEntity}",
                    ["fromEntityAttribute"] = fromAttribute,
                    ["toEntity"] = $"{toEntity}.cdm.json/{toEntity}",
                    ["toEntityAttribute"] = toAttribute
                });
            }

            string manifestFile = Path.Combine(outputDir, "default.manifest.cdm.json");
            File.WriteAllText(manifestFile, JsonConvert.SerializeObject(manifest, Formatting.Indented));
        }

        // Helper comparer (staat waarschijnlijk al in je code)
        class TupleStringIgnoreCaseComparer : IEqualityComparer<(string entity, string attr)>
        {
            public bool Equals((string entity, string attr) x, (string entity, string attr) y)
                => string.Equals(x.entity, y.entity, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.attr, y.attr, StringComparison.OrdinalIgnoreCase);

            public int GetHashCode((string entity, string attr) obj)
                => HashCode.Combine(
                    StringComparer.OrdinalIgnoreCase.GetHashCode(obj.entity ?? ""),
                    StringComparer.OrdinalIgnoreCase.GetHashCode(obj.attr ?? "")
                );
        }


        // ===== Helpers =====

        private static void PrecreateDomainFolders(string lb365Root)
        {
            foreach (var dom in AllDomains)
            {
                Directory.CreateDirectory(Path.Combine(lb365Root, dom, "Standard"));
                Directory.CreateDirectory(Path.Combine(lb365Root, dom, "Custom"));
            }
            Console.WriteLine("Vooraf alle LB365 domeinmappen (Standard/Custom) aangemaakt.");
        }

        private static bool IncludeByPrefix(string prefix, string logicalName)
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

        private static string ToCorpusPath(string fromFile, string toFile)
        {
            var from = new Uri(Path.GetFullPath(fromFile));
            var to = new Uri(Path.GetFullPath(toFile));
            var rel = Uri.UnescapeDataString(from.MakeRelativeUri(to).ToString());
            return rel.Replace('\\', '/');
        }
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
