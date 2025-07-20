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
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading.Tasks;

    class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("Stap 1: Ophalen metadata uit Dataverse en opslaan als solution-export.json");
            await ExportDataverseMetadataAsync();

            //Console.WriteLine("Stap 2: Manifest maken vanuit solution-export.json");
            //await CreateManifestAsync();
        }

        private static async Task ExportDataverseMetadataAsync()
        {
            string connectionString = "AuthType=ClientSecret;Url=https://vlavirgemdev.crm4.dynamics.com;clientid=x;clientsecret=x;tenantid=x;";
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

        private static async Task CreateManifestAsync()
        {
            var cdmCorpus = new CdmCorpusDefinition();

            cdmCorpus.SetEventCallback(new EventCallback
            {
                Invoke = (level, message) =>
                {
                    Console.WriteLine(message);
                }
            }, CdmStatusLevel.Warning);

            Console.WriteLine("Configure storage adapters");

            string pathFromExeToExampleRoot = "../../../../../../";

            cdmCorpus.Storage.Mount("local", new LocalAdapter(pathFromExeToExampleRoot + "2-create-manifest/sample-data"));
            cdmCorpus.Storage.DefaultNamespace = "local";

            cdmCorpus.Storage.Mount("cdm", new LocalAdapter(pathFromExeToExampleRoot + "example-public-standards"));

            Console.WriteLine("Lees solution-export.json in");
            string solutionJson = File.ReadAllText("solution-export.json");
            var solution = JsonConvert.DeserializeObject<SolutionExport>(solutionJson);

            Console.WriteLine("Maak placeholder manifest");
            CdmManifestDefinition manifestAbstract = cdmCorpus.MakeObject<CdmManifestDefinition>(CdmObjectType.ManifestDef, "tempAbstract");

            // Dynamisch toevoegen van entities op basis van solution-export.json
            foreach (var entity in solution.Entities)
            {
                string entityName = entity.LogicalName;

                // Pas dit pad aan als jouw CDM schema’s elders staan
                string cdmPath = $"cdm:/core/applicationCommon/foundationCommon/crmCommon/accelerators/healthCare/electronicMedicalRecords/{entityName}.cdm.json/{entityName}";

                manifestAbstract.Entities.Add(entityName, cdmPath);
            }

            var localRoot = cdmCorpus.Storage.FetchRootFolder("local");
            localRoot.Documents.Add(manifestAbstract);

            Console.WriteLine("Resolve the placeholder");
            var manifestResolved = await manifestAbstract.CreateResolvedManifestAsync("default", "");

            manifestResolved.Imports.Add("cdm:/foundations.cdm.json");

            Console.WriteLine("Save the documents");
            foreach (CdmEntityDeclarationDefinition eDef in manifestResolved.Entities)
            {
                var entDef = await cdmCorpus.FetchObjectAsync<CdmEntityDefinition>(eDef.EntityPath, manifestResolved);

                var part = cdmCorpus.MakeObject<CdmDataPartitionDefinition>(CdmObjectType.DataPartitionDef, $"{entDef.EntityName}-data-description");
                eDef.DataPartitions.Add(part);
                part.Explanation = "not real data, just for demo";

                var location = $"local:/{entDef.EntityName}/partition-data.csv";
                part.Location = cdmCorpus.Storage.CreateRelativeCorpusPath(location, manifestResolved);

                var csvTrait = part.ExhibitsTraits.Add("is.partition.format.CSV", false) as CdmTraitReference;
                csvTrait.Arguments.Add("columnHeaders", "true");
                csvTrait.Arguments.Add("delimiter", ",");

                string partPath = cdmCorpus.Storage.CorpusPathToAdapterPath(location);

                string header = "";
                foreach (CdmTypeAttributeDefinition att in entDef.Attributes)
                {
                    if (header != "")
                        header += ",";
                    header += att.Name;
                }

                Directory.CreateDirectory(cdmCorpus.Storage.CorpusPathToAdapterPath($"local:/{entDef.EntityName}"));
                File.WriteAllText(partPath, header);
            }

            await manifestResolved.SaveAsAsync($"{manifestResolved.ManifestName}.manifest.cdm.json", true);

            Console.WriteLine("Manifest creatie afgerond.");
        }
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