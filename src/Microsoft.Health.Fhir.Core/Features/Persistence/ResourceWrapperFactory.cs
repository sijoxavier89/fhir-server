﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Collections.Generic;
using EnsureThat;
using Microsoft.Health.Core.Features.Security;
using Microsoft.Health.Fhir.Core.Features.Compartment;
using Microsoft.Health.Fhir.Core.Features.Context;
using Microsoft.Health.Fhir.Core.Features.Definition;
using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.Core.Models;

namespace Microsoft.Health.Fhir.Core.Features.Persistence
{
    /// <summary>
    /// Provides a mechanism to create a <see cref="ResourceWrapper"/>.
    /// </summary>
    public class ResourceWrapperFactory : IResourceWrapperFactory
    {
        private readonly IRawResourceFactory _rawResourceFactory;
        private readonly ISearchIndexer _searchIndexer;
        private readonly IFhirRequestContextAccessor _fhirRequestContextAccessor;
        private readonly IClaimsExtractor _claimsExtractor;
        private readonly ICompartmentIndexer _compartmentIndexer;
        private readonly ISearchParameterDefinitionManager _searchParameterDefinitionManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="ResourceWrapperFactory"/> class.
        /// </summary>
        /// <param name="rawResourceFactory">The raw resource factory.</param>
        /// <param name="fhirRequestContextAccessor">The FHIR request context accessor.</param>
        /// <param name="searchIndexer">The search indexer used to generate search indices.</param>
        /// <param name="claimsExtractor">The claims extractor used to extract claims.</param>
        /// <param name="compartmentIndexer">The compartment indexer.</param>
        /// <param name="searchParameterDefinitionManager"> The search parameter definition manager.</param>
        public ResourceWrapperFactory(
            IRawResourceFactory rawResourceFactory,
            IFhirRequestContextAccessor fhirRequestContextAccessor,
            ISearchIndexer searchIndexer,
            IClaimsExtractor claimsExtractor,
            ICompartmentIndexer compartmentIndexer,
            ISearchParameterDefinitionManager searchParameterDefinitionManager)
        {
            EnsureArg.IsNotNull(rawResourceFactory, nameof(rawResourceFactory));
            EnsureArg.IsNotNull(searchIndexer, nameof(searchIndexer));
            EnsureArg.IsNotNull(fhirRequestContextAccessor, nameof(fhirRequestContextAccessor));
            EnsureArg.IsNotNull(claimsExtractor, nameof(claimsExtractor));
            EnsureArg.IsNotNull(compartmentIndexer, nameof(compartmentIndexer));
            EnsureArg.IsNotNull(searchParameterDefinitionManager, nameof(searchParameterDefinitionManager));

            _rawResourceFactory = rawResourceFactory;
            _searchIndexer = searchIndexer;
            _fhirRequestContextAccessor = fhirRequestContextAccessor;
            _claimsExtractor = claimsExtractor;
            _compartmentIndexer = compartmentIndexer;
            _searchParameterDefinitionManager = searchParameterDefinitionManager;
        }

        /// <inheritdoc />
        public ResourceWrapper Create(ResourceElement resource, bool deleted, bool keepMeta)
        {
            RawResource rawResource = _rawResourceFactory.Create(resource, keepMeta);
            IReadOnlyCollection<SearchIndexEntry> searchIndices = _searchIndexer.Extract(resource);
            string searchParamHash = _searchParameterDefinitionManager.GetSearchParameterHashForResourceType(resource.InstanceType);

            IFhirRequestContext fhirRequestContext = _fhirRequestContextAccessor.FhirRequestContext;

            return new ResourceWrapper(
                resource,
                rawResource,
                new ResourceRequest(fhirRequestContext.Method, fhirRequestContext.Uri),
                deleted,
                searchIndices,
                _compartmentIndexer.Extract(resource.InstanceType, searchIndices),
                _claimsExtractor.Extract(),
                searchParamHash);
        }
    }
}
