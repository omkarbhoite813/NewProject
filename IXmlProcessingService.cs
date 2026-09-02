using System;
using System.Collections.Generic;
using System.Text;
using XmlMiddleware.Domain.Models;

namespace XmlMiddleware.Application.Interfaces
{
    public interface IXmlProcessingService
    {
        CustomersXmlModel Deserialize(string xml);
        void Validate(CustomersXmlModel document);
        Task ProcessAsync(string fileName,byte[] fileBytes,CancellationToken cancellationToken = default);
    }
}
