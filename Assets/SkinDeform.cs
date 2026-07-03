using System;
using System.Collections.Generic;
using System.IO;
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
        oldVertices = GetVertices().Item1;
        vertexOffsets = new Vector3[oldVertices.Length];
        vertexJellyOffsets = new Vector3[oldVertices.Length];
        touchtimes = new float[oldVertices.Length];
        vertexJellyTouched = new bool[oldVertices.Length];
    }

    void FixedUpdate()
    {
        (Vector3[] vertices, Vector3[] normals) = GetVertices();
        bool setAnyVertex = false;
        bool indent = false;
        float currentTime = Time.time;
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
            //var transformedColl = transform.InverseTransformPoint(new Vector3(coll.transform.position.x * transform.localScale.x, coll.transform.position.y * transform.localScale.y, coll.transform.position.z * transform.localScale.z));

            for (int i = 0; i < vertices.Length; i++)
            {
                if (vertexOffsets[i].sqrMagnitude < MaxIndent && (JellyRadius == 0 || vertexJellyOffsets[i].sqrMagnitude < MaxIndent))
                {
                    //todo weird shit with scaled meshes
                    //Vector3 vert = vertices[i] + vertexOffsets[i];
                    Vector3 vert = transform.TransformPoint(vertices[i]) + vertexOffsets[i];
                    var diff = (vert - coll.transform.position);
                    //var diff = (vert - transformedColl);
                    var collSize = coll.bounds.extents.magnitude;
                    var diffLength = diff.magnitude;
                    if (diffLength < collSize)
                    {
                        vertexOffsets[i] -= transform.InverseTransformDirection((diff.normalized * ((diffLength - collSize) * IndentScale)));
                        touchtimes[i] = currentTime;
                        setAnyVertex = true;
                        indent = true;
                    }
                    else if (JellyRadius > 0
                        && !vertexJellyTouched[i]
                        && indent
                        && vertexOffsets[i] == Vector3.zero
                        && vel > VelocityThreshhold
                        && diffLength > (collSize)
                        && diffLength < (collSize + JellyRadius))
                    {
                        vertexJellyOffsets[i] = -normals[i] * ((diffLength - (collSize + JellyRadius)) * (IndentScale * JellyStrength * (1 + vel - VelocityThreshhold)));
                        touchtimes[i] = currentTime;
                        vertexJellyTouched[i] = true;
                        setAnyVertex = true;
                    }
                }
            }
            oldCollPos[c] = coll.transform.position;
        }

        for (int x = 0; x < vertexOffsets.Length; x++)
        {
            var time = (currentTime - (touchtimes[x] + RecoveryDelayTime)) / IndentRecoveryTime;
            var jtime = (currentTime - (touchtimes[x] + jellyBuildup)) / JellyRecoveryTime;

            vertexOffsets[x] = Vector3.Lerp(vertexOffsets[x], Vector3.zero, time);
            //todo try and lerp to far then fast back
            //vertexOffsets[x] = Vector3.Lerp(vertexOffsets[x], Vector3.Lerp(vertexOffsets[x], Vector3.zero, time), time);
            vertexJellyOffsets[x] = Vector3.Lerp(vertexJellyOffsets[x], Vector3.zero, jtime);
            if (jtime > 0.9f)
            {
                vertexJellyTouched[x] = false;
            }

            setAnyVertex |= time <= 1f;
        }
        if (setAnyVertex)
        {
            SetVertices();
        }
    }

    private void SetVertices()
    {
        float currentTime = Time.time;
        Vector3[] vertices = new Vector3[vertexOffsets.Length];
        for (int i = 0; i < vertices.Length; i++)
        {
            var jtime = (currentTime - (touchtimes[i])) / jellyBuildup;
            if (jtime < 1)
            {
                vertices[i] = oldVertices[i] + vertexOffsets[i] + Vector3.Lerp(Vector3.zero, vertexJellyOffsets[i], jtime);
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

    private Tuple<Vector3[], Vector3[]> GetVertices()
    {
        Vector3[] vertices;
        Vector3[] normals;
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

        return new(vertices, normals);
    }
}

[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
struct ExampleVertex
{
    public Vector3 pos;
    public Vector3 normal;
    public Vector4 tangent;
}
