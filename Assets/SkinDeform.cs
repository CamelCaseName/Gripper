using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using Unity.VisualScripting;
using UnityEngine;

public class SkinDeform : MonoBehaviour
{
    private const float jellyBuildup = 0.05f;
    private readonly List<Collider> colliders = new();
    private MeshFilter filter = null;
    private Mesh _mesh;
    private Mesh Mesh
    {
        get
        {
            if (_mesh == null)
            {
                if (renderer is SkinnedMeshRenderer srender)
                {
                    isSkinned = true;
                    _mesh = srender.sharedMesh;
                }
                else if (renderer.GetType() == typeof(MeshRenderer))
                {
                    if (filter == null)
                    {
                        filter = GetComponent<MeshFilter>();
                    }
                    _mesh = filter.sharedMesh;
                }
            }
            return _mesh;
            throw new InvalidDataException("no MeshRenderer or SkinnedMeshRenderer present on the object");
        }
    }
    bool isSkinned = false;
    private Vector3[] oldVertices;
    private Vector3[] vertexOffsets;
    private Vector3[] vertexJellyOffsets;
    private Vector3[] vertices;
    private Vector3[] normals;
    private bool[] vertexJellyTouched;
    private float[] touchtimes;
    public Renderer renderer = null;
    public float VelocityThreshhold = 0.03f;
    public float IndentScale = 0.01f;
    public float MaxIndent = 0.05f;
    public float IndentRecoveryTime = 2f;
    public float RecoveryDelayTime = 1f;
    public float JellyRadius = 0.1f;
    public float JellyRecoveryTime = 0.7f;
    public float JellyStrength = 1.1f;
    readonly List<Vector3> oldCollPos = new();
    readonly Vector3 zero = Vector3.zero;
    //Bounds scaledBounds;
    ExampleVertex[] verticesF;
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        foreach (var go in GameObject.FindGameObjectsWithTag("SkinColliders"))
        {
            if (go.TryGetComponent<SphereCollider>(out var coll))
            {
                colliders.Add(coll);
                oldCollPos.Add(coll.transform.position);
            }
        }
        GetVertices();
        oldVertices = new Vector3[vertices.Length];
        vertices.CopyTo(oldVertices, 0);
        vertexOffsets = new Vector3[vertices.Length];
        vertexJellyOffsets = new Vector3[vertices.Length];
        touchtimes = new float[vertices.Length];
        vertexJellyTouched = new bool[vertices.Length];
    }

    //only works with meshes which are scaled more or less uniformly!
    void Update()
    {
        GetVertices();
        bool setAnyVertex = false;
        bool indent = false;
        float currentTime = Time.time;
        var sindentscale = IndentScale;
        var sjellyradius = JellyRadius / transform.localScale.magnitude;
        var sjellystrength = JellyStrength;
        var smaxIdent = MaxIndent / transform.localScale.magnitude;
        //scaledBounds = new(renderer.bounds.center, renderer.bounds.size * (1 + jellyFaker));
        for (int c = 0; c < colliders.Count; c++)
        {
            Collider coll = colliders[c];
            if (!coll.bounds.Intersects(renderer.bounds)) //dont need scaled bounds here as the up wave can only start once we press into the obj
            {
                continue;
            }

            var velVec = (coll.transform.position - oldCollPos[c]);
            var vel = velVec.magnitude;
            //move collider to local space
            var transformedColl = transform.InverseTransformPoint(coll.transform.position);

            for (int i = 0; i < vertices.Length; i++)
            {
                var collSize = ((coll.bounds.extents.x / transform.localScale.x) + (coll.bounds.extents.y / transform.localScale.y) + (coll.bounds.extents.z / transform.localScale.z)) / 3;
                var checksize = collSize + sjellyradius;
                var vert = vertices[i];
                if (vert.x > (transformedColl.x + checksize) || vert.x < (transformedColl.x - checksize))
                {
                    continue;
                }
                if (vert.y > (transformedColl.y + checksize) || vert.y < (transformedColl.y - checksize))
                {
                    continue;
                }
                if (vert.z > (transformedColl.z + checksize) || vert.z < (transformedColl.z - checksize))
                {
                    continue;
                }
                if (vertexOffsets[i].sqrMagnitude >= smaxIdent || JellyRadius != 0 && vertexJellyOffsets[i].sqrMagnitude >= smaxIdent)
                {
                    continue;
                }

                var diff = (vert + vertexOffsets[i] - transformedColl);
                var diffLength = diff.magnitude;
                if (diffLength < collSize)
                {
                    //should already be local space
                    vertexOffsets[i] -= (diff.normalized * ((diffLength - collSize) * sindentscale));
                    touchtimes[i] = currentTime;
                    setAnyVertex = true;
                    indent = true;
                }
                else if (JellyRadius > 0
                    && !vertexJellyTouched[i]
                    && indent
                    && vertexOffsets[i] == zero
                    && vel > VelocityThreshhold
                    && diffLength > (collSize)
                    && diffLength < (checksize))
                {
                    vertexJellyOffsets[i] = -normals[i] * ((diffLength - (collSize + sjellyradius)) * (sindentscale * sjellystrength * (1 + vel - VelocityThreshhold)));
                    touchtimes[i] = currentTime;
                    vertexJellyTouched[i] = true;
                    setAnyVertex = true;
                }
            }
            oldCollPos[c] = coll.transform.position;
        }

        for (int x = 0; x < vertexOffsets.Length; x++)
        {
            var time = (currentTime - (touchtimes[x] + RecoveryDelayTime)) / IndentRecoveryTime;
            var jtime = (currentTime - (touchtimes[x] + jellyBuildup)) / JellyRecoveryTime;

            if (time >= 0 && time < 1)
            {
                vertexOffsets[x] = Lerp(vertexOffsets[x], zero, time);
            }
            //todo try and lerp to far then fast back
            //vertexOffsets[x] = Vector3.Lerp(vertexOffsets[x], Vector3.Lerp(vertexOffsets[x], zero, time), time);
            if (jtime >= 0)
            {
                if (jtime < 1)
                {
                    vertexJellyOffsets[x] = Lerp(vertexJellyOffsets[x], zero, jtime);
                }
                else
                {
                    vertexJellyTouched[x] = false;
                }
            }

            setAnyVertex |= time <= 1f;
        }
        if (setAnyVertex)
        {
            SetVertices();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector3 Lerp(Vector3 a, Vector3 b, float t)
    {
        Vector3 result = new()
        {
            x = a.x + (b.x - a.x) * t,
            y = a.y + (b.y - a.y) * t,
            z = a.z + (b.z - a.z) * t
        };
        return result;
    }

    private void SetVertices()
    {
        float currentTime = Time.time;
        for (int i = 0; i < vertices.Length; i++)
        {
            var jtime = (currentTime - (touchtimes[i])) / jellyBuildup;
            if (jtime >= 0 && jtime < 1)
            {
                vertices[i] = oldVertices[i] + vertexOffsets[i] + Lerp(zero, vertexJellyOffsets[i], jtime);
            }
            else
            {
                vertices[i] = oldVertices[i] + vertexOffsets[i] + vertexJellyOffsets[i];
            }
        }
        Mesh.SetVertices(vertices);
        //Mesh.RecalculateBounds();
        Mesh.RecalculateNormals();
    }

    private void GetVertices()
    {
        if (!isSkinned)
        {
            vertices = Mesh.vertices;
            normals = Mesh.normals;
        }
        else
        {
            var buffer = ((SkinnedMeshRenderer)renderer).GetVertexBuffer();
            if (buffer is not null)
            {
                vertices = new Vector3[buffer.count];
                verticesF = new ExampleVertex[buffer.count];
                buffer.GetData(verticesF);
                for (int i = 0; i < verticesF.Length; i++)
                {
                    vertices[i] = verticesF[i].pos;
                }

                normals = new Vector3[buffer.count];
                for (int i = 0; i < verticesF.Length; i++)
                {
                    vertices[i] = verticesF[i].normal;
                }
            }
            else
            {
                vertices = new Vector3[0];
                normals = new Vector3[0];
            }
        }
    }
}

[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
struct ExampleVertex
{
    public Vector3 pos;
    public Vector3 normal;
    public Vector4 tangent;
}
