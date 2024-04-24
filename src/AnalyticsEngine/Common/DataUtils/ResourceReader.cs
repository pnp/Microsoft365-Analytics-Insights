using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Common.DataUtils
{
    public class ResourceReader
    {
        private readonly Assembly _assembly;

        public ResourceReader(Assembly assembly)
        {
            _assembly = assembly;
        }

        public string ReadResourceStringFromExecutingAssembly(string resourcePath)
        {
            using (var stream = GetAssemblyManifest(resourcePath))

            using (var reader = new StreamReader(stream))
                return reader.ReadToEnd();
        }

        public byte[] ReadResourceBytes(string resourcePath)
        {
            using (var stream = GetAssemblyManifest(resourcePath))
            using (var reader = new BinaryReader(stream))
                return reader.ReadBytes((int)stream.Length);

        }

        public Stream GetAssemblyManifest(string resourcePath)
        {
            var stream = _assembly.GetManifestResourceStream(resourcePath);
            if (stream == null)
            {
                throw new ArgumentOutOfRangeException(nameof(resourcePath), $"No resource found by name '{resourcePath}'");
            }
            return stream;
        }

        public List<string> GetResourceNamesMatchingPathRoot(string resourcePathRoot)
        {
            return _assembly.GetManifestResourceNames().Where(x => x.StartsWith(resourcePathRoot)).ToList();
        }
    }
}
