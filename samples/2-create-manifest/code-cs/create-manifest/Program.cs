// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

namespace create_manifest
{
    using Microsoft.CommonDataModel.ObjectModel.Cdm;
    using Microsoft.CommonDataModel.ObjectModel.Enums;
    using Microsoft.CommonDataModel.ObjectModel.Storage;
    using Microsoft.CommonDataModel.ObjectModel.Utilities;
    using Microsoft.PowerPlatform.Dataverse.Client;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading.Tasks;

    class Program
    {
        static async Task Main(string[] args)
        {
            //Console.WriteLine("Stap 1: Ophalen metadata uit Dataverse en opslaan als solution-export.json");
            //await ExportDataverseMetadataAsync();

            Console.WriteLine("Stap 2: Manifest maken vanuit solution-export.json");
            string inputPath = @"C:\Users\IllyaVerheyden\Desktop\CDM\solution-export.json";
            string outputDir = @"C:\Users\IllyaVerheyden\Desktop\CDM\samples\2-create-manifest\code-cs\cdm-out";

            GenerateCdmFromSolutionExport(inputPath, outputDir);

            Console.WriteLine("CDM bestanden zijn gegenereerd!");
        }

        private static async Task ExportDataverseMetadataAsync()
        {
            string connectionString = "ph";
            var serviceClient = new ServiceClient(connectionString);
            if (!serviceClient.IsReady)
            {
                Console.WriteLine("Verbinding met Dataverse mislukt.");
                return;
            }

            Console.WriteLine("Verbinding met Dataverse succesvol.");

            var solutionExport = new SolutionExport
            {
                SolutionName = "MySolution",
                Entities = new List<SolutionEntity>()
            };

            // Ophalen van alle entiteiten metadata (inclusief attributen)
            var request = new Microsoft.Xrm.Sdk.Messages.RetrieveAllEntitiesRequest()
            {
                EntityFilters = Microsoft.Xrm.Sdk.Metadata.EntityFilters.Entity | Microsoft.Xrm.Sdk.Metadata.EntityFilters.Attributes,
                RetrieveAsIfPublished = true
            };

            var response = (Microsoft.Xrm.Sdk.Messages.RetrieveAllEntitiesResponse)serviceClient.Execute(request);

            foreach (var entityMetadata in response.EntityMetadata)
            {
                var attributes = new List<SolutionAttribute>();

                foreach (var attr in entityMetadata.Attributes)
                {
                    attributes.Add(new SolutionAttribute
                    {
                        Name = attr.LogicalName,
                        Type = attr.AttributeType.ToString()
                    });
                }

                solutionExport.Entities.Add(new SolutionEntity
                {
                    LogicalName = entityMetadata.LogicalName,
                    Attributes = attributes
                });
            }

            string json = JsonConvert.SerializeObject(solutionExport, Formatting.Indented);
            File.WriteAllText("solution-export.json", json);

            Console.WriteLine("Metadata geëxporteerd naar solution-export.json");
        }

        static void GenerateCdmFromSolutionExport(string inputPath, string outputDir)
        {
            var json = JObject.Parse(File.ReadAllText(inputPath));
            var entities = (JArray)json["Entities"]!;

            var manifest = new JObject
            {
                ["manifestName"] = "default",
                ["jsonSchemaSemanticVersion"] = "1.0.0",
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
                string entityName = char.ToUpper(logicalName[0]) + logicalName[1..];

                var attrs = new JArray();
                var csvHeader = "";

                foreach (var attr in (JArray)entityNode["Attributes"]!)
                {
                    string name = attr["Name"]!.ToString();
                    string type = attr["Type"]?.ToString() ?? "String";
                    string cdmType = MapToCdmType(type);

                    attrs.Add(new JObject
                    {
                        ["name"] = name,
                        ["dataType"] = cdmType
                    });

                    csvHeader += name + ",";
                }
                if (csvHeader.EndsWith(",")) csvHeader = csvHeader.TrimEnd(',');

                // Entity JSON
                var entityDoc = new JObject
                {
                    ["$schema"] = "../schema.cdm.json",
                    ["jsonSchemaSemanticVersion"] = "1.0.0",
                    ["imports"] = new JArray(
                        new JObject { ["corpusPath"] = "cdm:/foundations.cdm.json" }
                    ),
                    ["definitions"] = new JArray(new JObject
                    {
                        ["entityName"] = entityName,
                        ["extendsEntity"] = new JObject { ["entityReference"] = "CdmEntity" },
                        ["hasAttributes"] = attrs
                    })
                };

                string entityFile = Path.Combine(outputDir, $"{entityName}.cdm.json");
                File.WriteAllText(entityFile, JsonConvert.SerializeObject(entityDoc, Formatting.Indented));

                // Data folder + CSV
                string entityFolder = Path.Combine(outputDir, entityName);
                Directory.CreateDirectory(entityFolder);
                string csvPath = Path.Combine(entityFolder, "partition-data.csv");
                File.WriteAllText(csvPath, csvHeader + Environment.NewLine); // enkel headers

                // Manifest entry
                ((JArray)manifest["entities"]!).Add(new JObject
                {
                    ["type"] = "LocalEntity",
                    ["entityName"] = entityName,
                    ["entityPath"] = $"{entityName}.cdm.json/{entityName}",
                    ["dataPartitions"] = new JArray(new JObject
                    {
                        ["name"] = $"{entityName}Partition",
                        ["location"] = $"{entityName}/partition-data.csv",
                        ["traits"] = new JArray(new JObject
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

            string manifestFile = Path.Combine(outputDir, "default.manifest.cdm.json");
            File.WriteAllText(manifestFile, JsonConvert.SerializeObject(manifest, Formatting.Indented));
        }

        static string MapToCdmType(string dvType) => dvType switch
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

    // Zet deze classes eventueel apart in een eigen file / project
    public class SolutionExport
    {
        public string SolutionName { get; set; }
        public List<SolutionEntity> Entities { get; set; }
    }

    public class SolutionEntity
    {
        public string LogicalName { get; set; }
        public List<SolutionAttribute> Attributes { get; set; }
    }
    public class SolutionAttribute
    {
        public string Name { get; set; }
        public string Type { get; set; }
    }
}
