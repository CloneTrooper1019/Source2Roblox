﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

using Source2Roblox.FileSystem;
using Source2Roblox.Octree;
using Source2Roblox.Textures;
using Source2Roblox.World;
using Source2Roblox.World.Types;

using RobloxFiles.DataTypes;

namespace Source2Roblox.Geometry
{
    public class WorldGeometry
    {
        private const TextureFlags IGNORE = TextureFlags.Hint | TextureFlags.Sky | TextureFlags.Skip | TextureFlags.Trigger;

        public readonly Dictionary<string, ValveMaterial> Materials = new Dictionary<string, ValveMaterial>();
        public readonly List<HashSet<Face>> FaceClusters = new List<HashSet<Face>>();

        public readonly List<StaticProp> StaticProps = new List<StaticProp>();
        public readonly DetailProps DetailProps = null;

        public readonly ObjMesh Mesh = new ObjMesh(1f / Program.STUDS_TO_VMF);
        public readonly StringBuilder MtlBuilder = new StringBuilder();

        public readonly BSPFile Level;
        public readonly string ExportDir;

        public ValveMaterial RegisterMaterial(string material, GameMount game = null, Face face = null)
        {
            string mapName = Level.Name;

            if (material.StartsWith($"maps/{mapName}"))
            {
                while (true)
                {
                    var lastUnderscore = material.LastIndexOf('_');

                    if (lastUnderscore < 0)
                        break;

                    string number = material.Substring(lastUnderscore + 1);

                    if (int.TryParse(number, out int _))
                    {
                        material = material.Substring(0, lastUnderscore);
                        continue;
                    }

                    material = material.Replace($"maps/{mapName}/", "");
                    break;
                }
            }

            if (face != null)
                face.Material = material;

            if (material.ToLowerInvariant() == "tools/toolstrigger")
            {
                if (face != null)
                    face.Skip = true;

                return null;
            }

            if (!Materials.ContainsKey(material))
            {
                var saveInfo = new FileInfo(material);
                string saveDir = saveInfo.DirectoryName;

                Directory.CreateDirectory(saveDir);
                Console.WriteLine($"Fetching material {material}");

                var vmt = new ValveMaterial(material, game);
                Mesh.WriteLine_MTL($"newmtl", material);
                Materials[material] = vmt;

                string diffuse = vmt.DiffusePath;
                string bump = vmt.BumpPath;

                if (!string.IsNullOrEmpty(diffuse))
                {
                    string png = diffuse.Replace(".vtf", ".png");
                    Mesh.WriteLine_MTL("\tmap_Kd", png);
                    vmt.SaveVTF(diffuse, ExportDir);
                }

                if (!string.IsNullOrEmpty(bump))
                {
                    string png = bump.Replace(".vtf", ".png");
                    Mesh.WriteLine_MTL($"\tbump", png);
                    vmt.SaveVTF(bump, ExportDir);
                }
            }

            if (Materials.TryGetValue(material, out ValveMaterial result))
                return result;

            return null;
        }

        public WorldGeometry(BSPFile bsp, string exportDir, GameMount game = null)
        {
            string mapName = bsp.Name;

            var edges = bsp.Edges;
            var surfEdges = bsp.SurfEdges;

            var vertIndices = new List<uint>();
            var normIndices = bsp.VertNormalIndices;

            foreach (int surf in surfEdges)
            {
                uint edge;

                if (surf >= 0)
                    edge = edges[surf * 2];
                else
                    edge = edges[-surf * 2 + 1];

                vertIndices.Add(edge);
            }

            Level = bsp;
            ExportDir = exportDir;

            var vertOffsets = new Dictionary<int, Vector3>();
            var brushModels = bsp.BrushModels;

            foreach (var entity in bsp.Entities)
            {
                string className = entity.ClassName;
                string modelId = entity.Get<string>("model");

                if (modelId == null)
                    continue;

                if (!modelId.StartsWith("*"))
                    continue;

                if (!int.TryParse(modelId.Substring(1), out int modelIndex))
                    continue;

                if (modelIndex < 0 || modelIndex >= brushModels.Count)
                    continue;

                bool shouldSkip = false;

                if (className.StartsWith("trigger"))
                    shouldSkip = true;

                var model = brushModels[modelIndex];
                var origin = entity.Get<Vector3>("origin");

                if (origin == null)
                    continue;

                var offset = origin - model.Origin;
                int faceIndex = model.FirstFace;
                int numFaces = model.NumFaces;

                for (int i = 0; i < numFaces; i++)
                {
                    int faceId = faceIndex + i;
                    var face = bsp.Faces[faceId];

                    if (shouldSkip)
                    {
                        face.Skip = true;
                        continue;
                    }

                    var firstEdge = face.FirstEdge;
                    var numEdges = face.NumEdges;

                    for (int j = 0; j < numEdges; j++)
                    {
                        var vertId = (int)vertIndices[firstEdge + j];
                        vertOffsets[vertId] = offset;
                    }

                    face.EntityId = modelIndex;
                }

                model.Origin = origin;
                model.BoundsMin += offset;
                model.BoundsMax += offset;
            }

            int numVerts = 0;
            int numNorms = 0;
            int numUVs = 0;

            foreach (var face in bsp.Faces)
            {
                var numEdges = face.NumEdges;
                face.FirstNorm = numNorms;

                var texInfo = bsp.TexInfo[face.TexInfo];
                var texData = bsp.TexData[texInfo.TextureData];

                int stringIndex = texData.StringTableIndex;
                var matIndex = bsp.TexDataStringTable[stringIndex];

                face.Material = bsp.TexDataStringData[matIndex];
                numNorms += numEdges;
            }

            var faces = bsp.Faces.OrderBy(face => face.Material);
            var faceOctree = new Octree<Face>();
            numNorms = 0;

            foreach (Face face in faces)
            {
                int planeId = face.PlaneNum;
                int dispInfo = face.DispInfo;
                int numEdges = face.NumEdges;

                int firstEdge = face.FirstEdge;
                int firstNorm = face.FirstNorm;

                var texInfo = bsp.TexInfo[face.TexInfo];
                var texData = bsp.TexData[texInfo.TextureData];

                var texel = texInfo.TextureVecs;
                var size = texData.Size;

                var material = face.Material;
                var flags = texInfo.Flags;

                if ((flags & IGNORE) != TextureFlags.None)
                {
                    face.Skip = true;
                    continue;
                }

                var center = new Vector3();
                RegisterMaterial(material, game, face);

                if (face.Skip)
                    continue;

                if (face.DispInfo >= 0)
                {
                    var disp = bsp.Displacements[dispInfo];
                    Debug.Assert(face.NumEdges == 4);

                    var corners = new Vector3[4];
                    var startPos = disp.StartPosition;

                    var bestDist = float.MaxValue;
                    var bestIndex = -1;

                    for (int i = 0; i < 4; i++)
                    {
                        var vertIndex = (int)vertIndices[firstEdge + i];
                        var vertex = bsp.Vertices[vertIndex];

                        var dist = (vertex - startPos).Magnitude;
                        corners[i] = vertex;

                        if (dist < bestDist)
                        {
                            bestDist = dist;
                            bestIndex = i;
                        }
                    }

                    // Rotate the corners so the startPos
                    // is first in line in the loop.

                    if (bestIndex != 0)
                    {
                        var leftHalf = corners.Skip(bestIndex);
                        var rightHalf = corners.Take(bestIndex);

                        var slice = leftHalf.Concat(rightHalf);
                        corners = slice.ToArray();
                    }

                    var dispSize = (1 << disp.Power) + 1;
                    numEdges = (dispSize * dispSize);

                    for (int y = 0; y < dispSize; y++)
                    {
                        var rowSample = (float)y / (dispSize - 1);
                        var rowStart = corners[0].Lerp(corners[1], rowSample);
                        var rowEnd = corners[3].Lerp(corners[2], rowSample);

                        for (int x = 0; x < dispSize; x++)
                        {
                            var colSample = (float)x / (dispSize - 1);
                            var i = disp.DispVertStart + (y * dispSize) + x;

                            var pos = rowStart.Lerp(rowEnd, colSample);
                            var uv = texel.CalcTexCoord(pos, size);
                            var norm = new Vector3(0, 0, -1); // TODO

                            var dispVert = bsp.DispVerts[i];
                            pos += dispVert.Vector * dispVert.Dist;
                            center += (pos / numEdges);

                            Mesh.AddNormal(norm.X, norm.Z, -norm.Y);
                            Mesh.AddVertex(pos.X, pos.Z, -pos.Y);
                            Mesh.AddUV(uv.X, 1f - uv.Y);
                        }
                    }

                    Mesh.SetObject($"disp_{face.DispInfo}");
                    face.NumEdges = (short)numEdges;
                    face.Area = 0;
                }
                else
                {
                    for (int i = numEdges - 1; i >= 0; i--)
                    {
                        var vertIndex = (int)vertIndices[firstEdge + i];
                        var normIndex = (int)normIndices[firstNorm + i];

                        var vert = bsp.Vertices[vertIndex];
                        var norm = bsp.VertNormals[normIndex];
                        var uv = texel.CalcTexCoord(vert, size);

                        if (vertOffsets.ContainsKey(vertIndex))
                            vert += vertOffsets[vertIndex];

                        center += vert / numEdges;
                        Mesh.AddUV(uv.X, 1f - uv.Y);
                        Mesh.AddVertex(vert.X, vert.Z, -vert.Y);
                        Mesh.AddNormal(norm.X, norm.Z, -norm.Y);
                    }
                }

                face.Center = center;
                face.FirstUV = numUVs;
                face.FirstNorm = numNorms;
                face.FirstVert = numVerts;

                numUVs += numEdges;
                numNorms += numEdges;
                numVerts += numEdges;

                faceOctree.CreateNode(center, face);
            }

            // Cluster nearby faces by material.

            var facesLeft = faces
                .Where(face => !face.Skip)
                .ToHashSet();

            int faceTotal = facesLeft.Count,
                faceCount = 0;

            while (facesLeft.Any())
            {
                Console.WriteLine($"Clumping faces... {faceCount}/{faceTotal}");

                var face = facesLeft.First();
                facesLeft.Remove(face);

                var area = face.Area;
                var sqrtArea = Math.Sqrt(area);

                var searchArea = (int)Math.Max(500, sqrtArea * 5);
                var cluster = new HashSet<Face>() { face };

                var nearby = faceOctree
                    .RadiusSearch(face.Center, searchArea)
                    .Where(other => other.Material == face.Material)
                    .Where(facesLeft.Contains);

                foreach (var other in nearby)
                {
                    facesLeft.Remove(other);
                    cluster.Add(other);
                }

                FaceClusters.Add(cluster);
                faceCount += cluster.Count;
            }

            // Read game lumps
            var staticProps = bsp.FindGameLump("sprp");
            var detailProps = bsp.FindGameLump("dprp");
            
            if (staticProps != null)
            {
                using (var propStream = new MemoryStream(staticProps.Content))
                using (var reader = new BinaryReader(propStream))
                {
                    int numStrings = reader.ReadInt32();
                    var strings = new string[numStrings];

                    for (int i = 0; i < numStrings; i++)
                        strings[i] = reader.ReadString(128);

                    int numLeaves = reader.ReadInt32();
                    var leaves = new ushort[numLeaves];

                    for (int i = 0; i < numLeaves; i++)
                        leaves[i] = reader.ReadUInt16();

                    var numProps = reader.ReadInt32();
                    var props = new StaticProp[numProps];

                    for (int i = 0; i < numProps; i++)
                    {
                        var prop = new StaticProp(bsp, staticProps, reader);
                        prop.Name = strings[prop.PropType];
                        props[i] = prop;
                    }

                    StaticProps = props.ToList();
                }
            }
            
            if (detailProps != null)
            {
                using (var propStream = new MemoryStream(detailProps.Content))
                using (var reader = new BinaryReader(propStream))
                {
                    var props = new DetailProps(reader);
                    DetailProps = props;
                }
            }
        }
    }
}