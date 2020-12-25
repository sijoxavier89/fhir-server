﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.Azure.ContainerRegistry;
using Microsoft.Azure.ContainerRegistry.Models;
using Microsoft.Extensions.Options;
using Microsoft.Health.Fhir.Core.Configs;
using Microsoft.Health.Fhir.Core.Features.Operations;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.Fhir.Tests.Common.FixtureParameters;
using Microsoft.Health.Fhir.Tests.E2E.Common;
using Microsoft.Health.Fhir.Tests.E2E.Rest;
using Microsoft.Health.Test.Utilities;
using Microsoft.Rest;
using Xunit;
using Task = System.Threading.Tasks.Task;

namespace Microsoft.Health.Fhir.Shared.Tests.E2E.Rest
{
    /// <summary>
    /// Tests using customized template set will not run without extra container registry info
    /// since there is no acr emulator.
    /// </summary>
    [Trait(Traits.Category, Categories.CustomConvertData)]
    [HttpIntegrationFixtureArgumentSets(DataStore.All, Format.Json)]
    public class CustomConvertDataTests : IClassFixture<HttpIntegrationTestFixture>, IAsyncLifetime
    {
        private const string TestRepositoryName = "conversiontemplatestest";
        private const string TestRepositoryTag = "test1.0";
        private string testTemplateReference;

        private readonly TestFhirClient _testFhirClient;
        private readonly ConvertDataConfiguration _convertDataConfiguration;

        public CustomConvertDataTests(HttpIntegrationTestFixture fixture)
        {
            _testFhirClient = fixture.TestFhirClient;
            _convertDataConfiguration = ((IOptions<ConvertDataConfiguration>)(fixture.TestFhirServer as InProcTestFhirServer)?.Server?.Services?.GetService(typeof(IOptions<ConvertDataConfiguration>)))?.Value;
        }

        [Fact]
        public async Task GivenAValidRequestWithCustomizedTemplateSet_WhenConvertData_CorrectResponseShouldReturn()
        {
            var registry = GetTestContainerRegistryInfo();

            // Here we skip local E2E test since we need Managed Identity for container registry token.
            // We also skip the case when environmental variable is not provided (not able to upload templates)
            if (_convertDataConfiguration != null || registry == null)
            {
                return;
            }

            var parameters = GetConvertDataParams(GetSampleHl7v2Message(), "hl7v2", testTemplateReference, "ADT_A01");
            var requestMessage = GenerateConvertDataRequest(parameters);
            HttpResponseMessage response = await _testFhirClient.HttpClient.SendAsync(requestMessage);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var bundleContent = await response.Content.ReadAsStringAsync();
            var setting = new ParserSettings()
            {
                AcceptUnknownMembers = true,
                PermissiveParsing = true,
            };
            var parser = new FhirJsonParser(setting);
            var bundleResource = parser.Parse<Bundle>(bundleContent);
            Assert.Equal("urn:uuid:9d697ec3-48c3-3e17-db6a-29a1765e22c6", bundleResource.Entry.First().FullUrl);
        }

        [Theory]
        [InlineData("template@sha256:a665a45920422f9d417e4867efdc4fb8a04a1f3fff1fa07e998e86f7f7a27ae3")]
        [InlineData("wrongtemplate@sha256:03ac674216f3e15c761ee1a5e255f067953623c8b388b4459e13f978d7c846f4")]
        [InlineData("template@sha256:592535ef52d742f81e35f4d87b43d9b535ed56cf58c90a14fc5fd7ea0fbb8695")]
        public async Task GivenAValidRequest_ButTemplateSetIsNotFound_WhenConvertData_ShouldReturnError(string imageReference)
        {
            var registry = GetTestContainerRegistryInfo();

            // Here we skip local E2E test since we need Managed Identity for container registry token.
            // We also skip the case when environmental variable is not provided (not able to upload templates)
            if (_convertDataConfiguration != null || registry == null)
            {
                return;
            }

            var parameters = GetConvertDataParams(GetSampleHl7v2Message(), "hl7v2", $"{registry.Server}/{imageReference}", "ADT_A01");

            var requestMessage = GenerateConvertDataRequest(parameters);
            HttpResponseMessage response = await _testFhirClient.HttpClient.SendAsync(requestMessage);
            var responseContent = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains($"Image Not Found.", responseContent);
        }

        private ContainerRegistryInfo GetTestContainerRegistryInfo()
        {
            var containerRegistry = new ContainerRegistryInfo
            {
                Server = Environment.GetEnvironmentVariable("TestContainerRegistryServer"),
                Username = Environment.GetEnvironmentVariable("TestContainerRegistryServer")?.Split('.')[0],
                Password = Environment.GetEnvironmentVariable("TestContainerRegistryPassword"),
            };

            if (string.IsNullOrEmpty(containerRegistry.Server) || string.IsNullOrEmpty(containerRegistry.Password))
            {
                return null;
            }

            return containerRegistry;
        }

        private async System.Threading.Tasks.Task<string> GenerateCustomTemplateReference(ContainerRegistryInfo registry, string repository, string tag)
        {
            AzureContainerRegistryClient acrClient = new AzureContainerRegistryClient(registry.Server, new AcrBasicToken(registry));

            int schemaV2 = 2;
            string mediatypeV2Manifest = "application/vnd.docker.distribution.manifest.v2+json";
            string mediatypeV1Manifest = "application/vnd.oci.image.config.v1+json";
            string emptyConfigStr = "{}";

            // Upload config blob
            byte[] originalConfigBytes = Encoding.UTF8.GetBytes(emptyConfigStr);
            using var originalConfigStream = new MemoryStream(originalConfigBytes);
            string originalConfigDigest = ComputeDigest(originalConfigStream);
            await UploadBlob(acrClient, originalConfigStream, repository, originalConfigDigest);

            // Upload memory blob
            using Stream byteStream = Samples.GetDefaultConversionTemplates();
            var blobLength = byteStream.Length;
            string blobDigest = ComputeDigest(byteStream);
            await UploadBlob(acrClient, byteStream, repository, blobDigest);

            // Push manifest
            List<Descriptor> layers = new List<Descriptor>
            {
                new Descriptor("application/vnd.oci.image.layer.v1.tar", blobLength, blobDigest),
            };
            var v2Manifest = new V2Manifest(schemaV2, mediatypeV2Manifest, new Descriptor(mediatypeV1Manifest, originalConfigBytes.Length, originalConfigDigest), layers);
            await acrClient.Manifests.CreateAsync(repository, tag, v2Manifest);

            var imageAttributes = await acrClient.Tag.GetAttributesAsync(repository, tag);
            var digest = imageAttributes.Attributes.Digest;

            return $"{registry.Server}/{repository}@{digest}";
        }

        private async Task DeleteCustomTemplate(ContainerRegistryInfo registry, string repository, string tag)
        {
            AzureContainerRegistryClient acrClient = new AzureContainerRegistryClient(registry.Server, new AcrBasicToken(registry));
            await acrClient.Tag.DeleteAsync(repository, tag);
        }

        private async Task UploadBlob(AzureContainerRegistryClient acrClient, Stream stream, string repository, string digest)
        {
            stream.Position = 0;
            var uploadInfo = await acrClient.Blob.StartUploadAsync(repository);
            var uploadedLayer = await acrClient.Blob.UploadAsync(stream, uploadInfo.Location);
            await acrClient.Blob.EndUploadAsync(digest, uploadedLayer.Location);
        }

        private static string ComputeDigest(Stream s)
        {
            s.Position = 0;
            StringBuilder sb = new StringBuilder();

            using (var hash = SHA256.Create())
            {
                byte[] result = hash.ComputeHash(s);
                foreach (byte b in result)
                {
                    sb.Append(b.ToString("x2"));
                }
            }

            return "sha256:" + sb.ToString();
        }

        private HttpRequestMessage GenerateConvertDataRequest(
            Parameters inputParameters,
            string path = "$convert-data")
        {
            var request = new HttpRequestMessage
            {
                Method = HttpMethod.Post,
            };

            request.Content = new StringContent(inputParameters.ToJson(), System.Text.Encoding.UTF8, "application/json");
            request.RequestUri = new Uri(_testFhirClient.HttpClient.BaseAddress, path);

            return request;
        }

        private static Parameters GetConvertDataParams(string inputData, string inputDataType, string templateSetReference, string rootTemplate)
        {
            var parametersResource = new Parameters();
            parametersResource.Parameter = new List<Parameters.ParameterComponent>();

            parametersResource.Parameter.Add(new Parameters.ParameterComponent() { Name = ConvertDataProperties.InputData, Value = new FhirString(inputData) });
            parametersResource.Parameter.Add(new Parameters.ParameterComponent() { Name = ConvertDataProperties.InputDataType, Value = new FhirString(inputDataType) });
            parametersResource.Parameter.Add(new Parameters.ParameterComponent() { Name = ConvertDataProperties.TemplateCollectionReference, Value = new FhirString(templateSetReference) });
            parametersResource.Parameter.Add(new Parameters.ParameterComponent() { Name = ConvertDataProperties.RootTemplate, Value = new FhirString(rootTemplate) });

            return parametersResource;
        }

        private static string GetSampleHl7v2Message()
        {
            return "MSH|^~\\&|SIMHOSP|SFAC|RAPP|RFAC|20200508131015||ADT^A01|517|T|2.3|||AL||44|ASCII\nEVN|A01|20200508131015|||C005^Whittingham^Sylvia^^^Dr^^^DRNBR^PRSNL^^^ORGDR|\nPID|1|3735064194^^^SIMULATOR MRN^MRN|3735064194^^^SIMULATOR MRN^MRN~2021051528^^^NHSNBR^NHSNMBR||Kinmonth^Joanna^Chelsea^^Ms^^CURRENT||19870624000000|F|||89 Transaction House^Handmaiden Street^Wembley^^FV75 4GJ^GBR^HOME||020 3614 5541^HOME|||||||||C^White - Other^^^||||||||\nPD1|||FAMILY PRACTICE^^12345|\nPV1|1|I|OtherWard^MainRoom^Bed 183^Simulated Hospital^^BED^Main Building^4|28b|||C005^Whittingham^Sylvia^^^Dr^^^DRNBR^PRSNL^^^ORGDR|||CAR|||||||||16094728916771313876^^^^visitid||||||||||||||||||||||ARRIVED|||20200508131015||";
        }

        public async Task InitializeAsync()
        {
            var registry = GetTestContainerRegistryInfo();
            if (registry != null)
            {
                testTemplateReference = await GenerateCustomTemplateReference(registry, TestRepositoryName, TestRepositoryTag);
            }
        }

        public async Task DisposeAsync()
        {
            var registry = GetTestContainerRegistryInfo();
            if (registry != null)
            {
                await DeleteCustomTemplate(registry, TestRepositoryName, TestRepositoryTag);
            }
        }

        internal class ContainerRegistryInfo
        {
            public string Server { get; set; }

            public string Username { get; set; }

            public string Password { get; set; }
        }

        internal class AcrBasicToken : ServiceClientCredentials
        {
            private ContainerRegistryInfo _registry;

            public AcrBasicToken(ContainerRegistryInfo registry)
            {
                _registry = registry;
            }

            public override void InitializeServiceClient<T>(ServiceClient<T> client)
            {
                base.InitializeServiceClient(client);
            }

            public override Task ProcessHttpRequestAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var basicToken = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_registry.Username}:{_registry.Password}"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basicToken);
                return base.ProcessHttpRequestAsync(request, cancellationToken);
            }
        }
    }
}
