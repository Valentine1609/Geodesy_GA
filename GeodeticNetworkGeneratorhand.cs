using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.LinearAlgebra.Double;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using TMPro;
using Unity.VisualScripting;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using Debug = UnityEngine.Debug;


public class GeodeticNetworkGeneratorhand : MonoBehaviour
{
    [Header("Настройки")]
    public float offset = 5f;
    public Transform parentContainer;

    [Header("Raycast")]
    public float raycastHeight = 5f;
    public LayerMask groundLayer;
    public LayerMask obstacleLayer;

    [Header("py файл (указываем через Inspector)")]
    public string pythonFilePath;

    [Header("СКО")]
    public TMP_InputField directionStdevInput;

    [Header("UI")]
    public TMP_Text resultText;
    public TMP_InputField distanceStdevInput;
    public TMP_InputField coordNoiseInput;

    [Header("Геометрическая устойчивость")]
    public bool showWeakLinks = true;
    public float gizmoSphereSize = 0.3f;
    public float condScale = 1000f;
    private List<(Vector3 pos, double cond)> lastSuggestions;

    private class Station
    {
        public Vector3 position;
        public GameObject networkObj;
        public Transform ms60;
        public HashSet<Transform> visibleGrades = new HashSet<Transform>();
    }

    private List<Station> selectedStations = new List<Station>();
    private List<Transform> grades = new List<Transform>();
    private Vector3 groundPos;
    private float alignmentPenalty;

    private void Awake()
    {
        if (parentContainer == null) parentContainer = this.transform;
    }

    public void EvaluateNetworkStability(int topK = 5)
    {
        lastSuggestions = SuggestStationPositions(topK);

        if (lastSuggestions == null || lastSuggestions.Count == 0)
        {
            Debug.Log("Нет предложений по улучшению сети.");
            return;
        }

        foreach (var s in lastSuggestions)
        {
            Debug.Log($"Кандидат: {s.pos}, cond = {s.cond:F2}");

            if (showWeakLinks)
            {
                GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                sphere.transform.position = s.pos + Vector3.up * 0.2f;
                sphere.transform.localScale = Vector3.one * gizmoSphereSize;

                float t = Mathf.Clamp01((float)((s.cond - 1.0) / condScale));
                sphere.GetComponent<Renderer>().material.color = Color.Lerp(Color.green, Color.red, t);

                Destroy(sphere.GetComponent<Collider>());
                sphere.name = $"CandidateCond_{s.cond:F0}";
            }
        }
    }

    public void OnCheckStabilityButton()
    {
        EvaluateNetworkStability(topK: 5);
    }

    public void OnGenerateButtonPressed()
    {
        GenerateNetwork();
        ExportXML();
        RunPython();
        PrintPointAposterioriFromUnity();
        PrintNetworkReport();
    }

    private void GenerateNetwork()
    {
        // --- Очистка предыдущих линий ---
        foreach (Transform t in parentContainer)
            if (t.name == "Line" || t.name.StartsWith("CandidateCond_") || t.name == "GeodeticLine")
                Destroy(t.gameObject);

        selectedStations.Clear();

        var reMS60 = new Regex(@"(?:MS60|total_station)\s*\((\d+)\)");
        var reGrade = new Regex(@"Grade\s*\((\d+)\)");

        // --- Сбор пронумерованных MS60 ---
        var msIndexed = new List<(int idx, Transform totalStationTransform)>();
        foreach (var go in GameObject.FindGameObjectsWithTag("total_station"))
        {
            Transform cur = go.transform;
            while (cur != null)
            {
                var m = reMS60.Match(cur.name ?? "");
                if (m.Success && int.TryParse(m.Groups[1].Value, out int idx))
                {
                    msIndexed.Add((idx, go.transform));
                    break;
                }
                cur = cur.parent;
            }
        }
        msIndexed.Sort((a, b) => a.idx.CompareTo(b.idx));

        // --- Сбор пронумерованных Grade ---
        var gradeIndexed = new List<(int idx, Transform gradeTransform)>();
        foreach (var go in GameObject.FindGameObjectsWithTag("grade"))
        {
            var m = reGrade.Match(go.name ?? "");
            if (!m.Success) continue;
            if (!int.TryParse(m.Groups[1].Value, out int idx)) continue;
            gradeIndexed.Add((idx, go.transform));
        }
        gradeIndexed.Sort((a, b) => a.idx.CompareTo(b.idx));

        if (msIndexed.Count == 0 || gradeIndexed.Count == 0)
        {
            Debug.LogWarning("Нет пронумерованных MS60(n) или Grade(n).");
            return;
        }

        // --- Формируем станции ---
        foreach (var pair in msIndexed)
            selectedStations.Add(new Station
            {
                position = pair.totalStationTransform.position,
                networkObj = pair.totalStationTransform.gameObject,
                ms60 = pair.totalStationTransform,
                visibleGrades = new HashSet<Transform>()
            });

        grades = gradeIndexed.Select(x => x.gradeTransform).ToList();

        // --- Линии станция -> марка ---
        foreach (var s in selectedStations)
        {
            Vector3 from = s.ms60.position;
            foreach (var g in grades)
            {
                if (HasLineOfSight(s.ms60, g))
                {
                    CreateLineBetween(s.ms60.position, g.position, true);
                    s.visibleGrades.Add(g);
                }
            }
        }

        // --- Линии между станциями ---
        for (int i = 0; i < selectedStations.Count; i++)
        {
            Vector3 from = selectedStations[i].ms60.position;
            for (int j = i + 1; j < selectedStations.Count; j++)
                if (HasLineOfSight(
                        selectedStations[i].ms60,
                        selectedStations[j].ms60))
                {
                    CreateLineBetween(
                        selectedStations[i].ms60.position,
                        selectedStations[j].ms60.position,
                        true
                    );
                }
        }

        Debug.Log($"GenerateNetwork: построено линий (станция->марка): stations={selectedStations.Count}, grades={grades.Count}");
        PrintNetworkReport();
    }


    private bool HasLineOfSight(Transform from, Transform to)
    {
        const float instrumentHeight = 1.7f;

        Vector3 start = from.position + Vector3.up * instrumentHeight;
        Vector3 end = to.position + Vector3.up * instrumentHeight; // ✅ Одинаковая высота для станций

        // ✅ Проверяем тег цели для правильной обработки
        bool isStation = to.CompareTag("total_station");
        bool isGrade = to.CompareTag("grade");

        // Для станций используем позицию трансформера
        if (isStation)
        {
            return CheckLineOfSightBetweenPoints(start, end, to);
        }

        // Для марок пробуем найти коллайдер
        Collider col = to.GetComponent<Collider>();
        if (col != null)
        {
            end = col.bounds.center;
        }

        return CheckLineOfSightBetweenPoints(start, end, to);
    }
    // --- Построение линии ---
    private void CreateLineBetween(Vector3 start, Vector3 end, bool visible)
    {
        GameObject lineObj = new GameObject("GeodeticLine");
        lineObj.transform.SetParent(parentContainer, true);

        LineRenderer lr = lineObj.AddComponent<LineRenderer>();
        lr.useWorldSpace = true; // КРИТИЧЕСКИ ВАЖНО для корректного отображения в мировых координатах

        // Устанавливаем позиции напрямую (без массива)
        lr.positionCount = 2;
        lr.SetPosition(0, start);
        lr.SetPosition(1, end);

        // Создаём материал с резервными вариантами шейдера
        Shader shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
        if (shader == null) shader = Shader.Find("Particles/Unlit");
        if (shader == null) shader = Shader.Find("Unlit/Color");
        if (shader == null) shader = Shader.Find("Standard");

        lr.material = new Material(shader);
        lr.startWidth = 0.03f;  // Чуть толще для надёжной видимости
        lr.endWidth = 0.03f;
        lr.numCapVertices = 4;  // Сглаженные концы

        // ИСПРАВЛЕНО: используем простые цвета вместо градиента (градиент не работает со всеми шейдерами)
        Color color = visible ? new Color(0f, 1f, 0f, 0.9f) : new Color(1f, 0f, 0f, 0.7f);
        lr.startColor = color;
        lr.endColor = color;

        // Для видимости в VR
        lr.alignment = LineAlignment.View;
    }

     private bool CheckLineOfSightBetweenPoints(Vector3 from, Vector3 to, Transform target)
    {
        Vector3 dir = to - from;
        float dist = dir.magnitude;

        if (dist < 1e-6f)
            return true;

        RaycastHit[] hits = Physics.RaycastAll(from, dir.normalized, dist);

        // 🔴 КРИТИЧЕСКОЕ ИСПРАВЛЕНИЕ — сортировка
        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

        foreach (var hit in hits)
        {
            Transform t = hit.transform;

            // ✅ Игнорируем станцию (источник и цель)
            if (t.CompareTag("total_station"))
                continue;

            // ✅ Игнорируем марку-цель
            if (t == target || t.IsChildOf(target))
                return true;

            // ❌ Встретили препятствие раньше цели
            return false;
        }

        return true; // ✅ Если нет попаданий — видимость есть
    }

    private string ConvertDegreesToDMS(float degrees)
    {
        int d = Mathf.FloorToInt(degrees);
        float remainder = (degrees - d) * 60f;
        int m = Mathf.FloorToInt(remainder);
        float s = (remainder - m) * 60f;
        return $"{d}-{m}-{s.ToString("F2", CultureInfo.InvariantCulture)}";
    }

    public float DistanceStdevMm
    {
        get
        {
            if (distanceStdevInput != null &&
                float.TryParse(distanceStdevInput.text.Replace(",", "."), NumberStyles.Float, CultureInfo.InvariantCulture, out float val))
            {
                return val;
            }
            return 5.0f;
        }
    }

    private float DirectionStdevCc
    {
        get
        {
            if (directionStdevInput != null &&
                float.TryParse(directionStdevInput.text.Replace(",", "."), NumberStyles.Float, CultureInfo.InvariantCulture, out float val))
            {
                return val;
            }
            return 5.0f;
        }
    }

    private void ExportXML()
    {
        if (selectedStations.Count == 0 || grades.Count == 0)
        {
            Debug.LogWarning("Нет данных для экспорта XML");
            return;
        }

        var xml = new StringBuilder();

        xml.AppendLine("<?xml version=\"1.0\" ?>");
        xml.AppendLine("<gama-local xmlns=\"http://www.gnu.org/software/gama/gama-local\">");
        xml.AppendLine("  <network axes-xy=\"ne\" angles=\"right-handed\">");
        xml.AppendLine("    <parameters sigma-apr=\"1.0\" sigma-act=\"apriori\" angles=\"360\"/>");
        xml.AppendLine("    <points-observations>");

        // === Начало координат — первая станция ===
        Vector3 origin = selectedStations[0].ms60.position;
        float coordNoise = CoordNoiseMm;

        // === Экспорт станций ===
        for (int i = 0; i < selectedStations.Count; i++)
        {
            Vector3 rel = selectedStations[i].ms60.position - origin;

            float noiseX = UnityEngine.Random.Range(-coordNoise, coordNoise) / 1000f;
            float noiseZ = UnityEngine.Random.Range(-coordNoise, coordNoise) / 1000f;

            float x = (i == 0) ? 0f : rel.x + noiseX;
            float y = (i == 0) ? 0f : rel.z + noiseZ;

            xml.AppendLine(
                $"      <point id=\"T{i + 1}\" " +
                $"x=\"{x.ToString("F3", CultureInfo.InvariantCulture)}\" " +
                $"y=\"{y.ToString("F3", CultureInfo.InvariantCulture)}\" fix=\"xy\" />"
            );
        }

        // === Экспорт марок ===
        for (int g = 0; g < grades.Count; g++)
        {
            xml.AppendLine($"      <point id=\"G{g + 1}\" adj=\"xy\" />");
        }

        // === Экспорт наблюдений ===
        for (int s = 0; s < selectedStations.Count; s++)
        {
            Station station = selectedStations[s];
            string fromId = $"T{s + 1}";
            Vector3 fromPos = station.ms60.position;

            xml.AppendLine($"      <obs from=\"{fromId}\">");

            // ===== станция → марка =====
            foreach (Transform grade in station.visibleGrades)
            {
                int gIndex = grades.IndexOf(grade);
                if (gIndex < 0) continue;

                string toId = $"G{gIndex + 1}";
                Vector3 toPos = grade.position;

                float az = CalculateAzimuthDegrees(fromPos, toPos);
                string azDms = ConvertDegreesToDMS(az);
                float dist = Vector3.Distance(fromPos, toPos);

                xml.AppendLine(
                    $"        <direction to=\"{toId}\" val=\"{azDms}\" stdev=\"{DirectionStdevCc}\" />"
                );
                xml.AppendLine(
                    $"        <distance to=\"{toId}\" val=\"{dist.ToString("F3", CultureInfo.InvariantCulture)}\" stdev=\"{DistanceStdevMm}\" />"
                );
            }

            // ===== станция ↔ станция =====
            for (int t = 0; t < selectedStations.Count; t++)
            {
                if (t == s) continue;

                Transform toStation = selectedStations[t].ms60;

                // проверяем прямую видимость ТОЛЬКО между станциями
                if (!HasLineOfSight(station.ms60, toStation))
                    continue;

                string toId = $"T{t + 1}";
                Vector3 toPos = toStation.position;

                float az = CalculateAzimuthDegrees(fromPos, toPos);
                string azDms = ConvertDegreesToDMS(az);
                float dist = Vector3.Distance(fromPos, toPos);

                xml.AppendLine(
                    $"        <direction to=\"{toId}\" val=\"{azDms}\" stdev=\"{DirectionStdevCc}\" />"
                );
                xml.AppendLine(
                    $"        <distance to=\"{toId}\" val=\"{dist.ToString("F3", CultureInfo.InvariantCulture)}\" stdev=\"{DistanceStdevMm}\" />"
                );
            }

            xml.AppendLine("      </obs>");
        }

        xml.AppendLine("    </points-observations>");
        xml.AppendLine("  </network>");
        xml.AppendLine("</gama-local>");

        string savePath = Path.Combine(Application.dataPath, "Scenes/GNU/geodetic_network.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(savePath));
        File.WriteAllText(savePath, xml.ToString(), Encoding.UTF8);

        Debug.Log($"XML сохранён: {savePath}");
    }


    private float CoordNoiseMm
    {
        get
        {
            if (coordNoiseInput != null &&
                float.TryParse(coordNoiseInput.text.Replace(",", "."), NumberStyles.Float, CultureInfo.InvariantCulture, out float val))
            {
                return val;
            }
            return 5.0f;
        }
    }

    private float CalculateAzimuthDegrees(Vector3 from, Vector3 to)
    {
        Vector3 dir = to - from;
        float angle = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
        return (angle + 360f) % 360f;
    }

    [System.Serializable]
    public class PythonResult
    {
        public string status;
        public string[] errors;
        public Outputs outputs;
    }
    public struct ConditioningResult
    {
        public double conditionNumber;
        public double lambdaMin;
        public double lambdaMax;
    }

    [System.Serializable]
    public class Outputs
    {
        public string stdout;
        public string stderr;
        public string report;
    }
    // ==========================================================================
    //                           NETWORK REPORT (3D, с анализом геометрии)
    // ==========================================================================

    public void PrintNetworkReport()
    {
        if (grades == null || grades.Count == 0)
        {
            Debug.LogWarning("Нет марок — отчёт невозможен.");
            return;
        }

        if (selectedStations.Count < 2)
        {
            Debug.LogWarning($"Недостаточно станций: {selectedStations.Count}");
            return;
        }

        // ===== 1. СЫРАЯ НОРМАЛЬНАЯ МАТРИЦА =====
        double[,] N_raw = BuildRawNormalMatrix();
        if (N_raw == null || N_raw.GetLength(0) == 0)
        {
            Debug.LogError("Не удалось построить нормальную матрицу.");
            return;
        }

        // ===== 2. СПЕКТРАЛЬНЫЙ АНАЛИЗ =====
        if (!JacobiEigenDecomposition(N_raw, out double[] eig_raw, out _))
        {
            Debug.LogError("Не удалось выполнить спектральный анализ сырой матрицы.");
            return;
        }

        Array.Sort(eig_raw);

        // ✅ ОБЪЯВЛЯЕМ threshold ОДИН РАЗ в начале метода
        double threshold = eig_raw[eig_raw.Length - 1] * 1e-12;

        int zeroCount = 0;
        for (int i = 0; i < Math.Min(7, eig_raw.Length); i++)
            if (eig_raw[i] < threshold) zeroCount++;

        Debug.Log("=== АНАЛИЗ СПЕКТРА СОБСТВЕННЫХ ЗНАЧЕНИЙ ===");
        Debug.Log($"Неопределённых степеней свободы: {zeroCount}/7");
        if (zeroCount > 0)
            Debug.LogWarning($"СЕТЬ ИМЕЕТ {zeroCount} НЕОПРЕДЕЛЁННЫХ СТЕПЕНЕЙ СВОБОДЫ!");

        // ===== 3. ЧИСЛО ОБУСЛОВЛЕННОСТИ ГЕОМЕТРИИ =====
        int startIdx = 7;
        double cond_geometric = double.PositiveInfinity;

        if (eig_raw.Length > startIdx)
        {
            double lambda_min = eig_raw[startIdx];
            double lambda_max = eig_raw[eig_raw.Length - 1];

            // ✅ ИСПОЛЬЗУЕМ существующий threshold (не объявляем заново!)
            cond_geometric = (lambda_min < threshold) ? double.PositiveInfinity : lambda_max / lambda_min;
        }

        // ===== 4. КОВАРИАЦИОННАЯ МАТРИЦА =====
        if (!ComputeConditionNumberAndInverse(out _, out double[,] invN) || invN == null)
        {
            Debug.LogError("Не удалось получить ковариационную матрицу (N⁻¹).");
            return;
        }

        int n = invN.GetLength(0);

        // ===== 5. ОТЧЁТ =====
        Debug.Log("\n=== ОТЧЁТ О ГЕОМЕТРИИ И ТОЧНОСТИ СЕТИ ===");
        Debug.Log($"Марок: {grades.Count}");
        Debug.Log($"Станций: {selectedStations.Count}");
        Debug.Log($"Число обусловленности (геометрия сети): {cond_geometric:N0}");

        if (double.IsInfinity(cond_geometric) || cond_geometric > 1e8)
            Debug.LogError("Геометрия сети: КАТАСТРОФИЧЕСКИ ПЛОХАЯ (вырожденная)");
        else if (cond_geometric < 1e3)
            Debug.Log("Геометрия сети: ОТЛИЧНАЯ");
        else if (cond_geometric < 5e3)
            Debug.Log("Геометрия сети: ХОРОШАЯ");
        else if (cond_geometric < 1e5)
            Debug.LogWarning("Геометрия сети: ДОПУСТИМАЯ (но требует внимания)");
        else
            Debug.LogError("Геометрия сети: СЛАБАЯ (риск потери точности)");

        // ===== 6. ОЦЕНКА ТОЧНОСТИ МАРОК =====
        Debug.Log("\n=== ОЦЕНКА ОТНОСИТЕЛЬНОЙ ТОЧНОСТИ (σ₀ = 1) ===");

        double maxSigma = 0, sumSigma = 0;
        int count = 0;
        int offset = 3 * selectedStations.Count;

        for (int i = 0; i < grades.Count; i++)
        {
            int k = offset + i * 3;
            if (k + 2 >= n) continue;

            double sx = Math.Sqrt(Math.Max(0, invN[k, k]));
            double sy = Math.Sqrt(Math.Max(0, invN[k + 1, k + 1]));
            double sz = Math.Sqrt(Math.Max(0, invN[k + 2, k + 2]));

            double sigmaPlan = Math.Sqrt(sx * sx + sz * sz);
            double sigma3D = Math.Sqrt(sx * sx + sy * sy + sz * sz);

            maxSigma = Math.Max(maxSigma, sigma3D);
            sumSigma += sigma3D;
            count++;

            Debug.Log(
                $"Марка {grades[i].name}: " +
                $"σX={FormatSigmaMm(sx)}, σY={FormatSigmaMm(sy)}, σZ={FormatSigmaMm(sz)}, " +
                $"σплан={FormatSigmaMm(sigmaPlan)}, σ3D={FormatSigmaMm(sigma3D)}"
            );
        }

        if (count > 0)
        {
            Debug.Log($"\nСредняя σ3D: {FormatSigmaMm(sumSigma / count)}");
            Debug.Log($"Максимальная σ3D: {FormatSigmaMm(maxSigma)}");
        }

        // ===== 7. АНАЛИЗ ПОКРЫТИЯ =====
        Debug.Log("\n=== АНАЛИЗ ПОКРЫТИЯ ===");
        var covered = new HashSet<Transform>();
        foreach (var st in selectedStations)
            if (st.visibleGrades != null)
                covered.UnionWith(st.visibleGrades);

        Debug.Log($"Покрыто марок: {covered.Count}/{grades.Count}");

        var uncovered = grades.Where(g => !covered.Contains(g)).ToList();
        if (uncovered.Count > 0)
            Debug.LogWarning("Непокрытые марки: " + string.Join(", ", uncovered.Select(g => g.name)));

        // ===== 8. АНАЛИЗ ГЕОМЕТРИИ =====
        AnalyzeGeometry();

        Debug.Log("\n=== ОТЧЁТ ЗАВЕРШЁН ===");
    }
    private string FormatSigmaMm(double sigmaMeters)
    {
        double mm = sigmaMeters * 1000.0;
        if (double.IsNaN(mm) || double.IsInfinity(mm)) return "n/a";
        if (mm >= 0.05) return $"{mm:F1}мм";
        if (mm >= 0.0001) return $"{mm:F4}мм";
        return $"{mm:E2}мм";
    }

    // ==========================================================================
    //         CONDITION NUMBER + INVERSE (3D, Unity)
    // ==========================================================================

    private bool ComputeConditionNumberAndInverse(out double condValue, out double[,] invN)
    {
        condValue = double.PositiveInfinity;
        invN = null;

        // ✅ ИСПРАВЛЕНО: out bool вместо out double
        double[,] N = ComputeNormalMatrix(out bool isDegenerate);
        if (N == null || isDegenerate) return false;

        int n = N.GetLength(0);

        if (!JacobiEigenDecomposition(N, out double[] eig, out double[,] eigVec))
        {
            Debug.LogWarning("Eigen decomposition failed.");
            return false;
        }

        double minEig = double.MaxValue;
        double maxEig = double.MinValue;
        for (int i = 0; i < eig.Length; i++)
        {
            if (eig[i] < minEig) minEig = eig[i];
            if (eig[i] > maxEig) maxEig = eig[i];
        }

        if (minEig <= 1e-15)
        {
            Debug.LogWarning("Минимальное собственное значение ~0 — сеть вырождена.");
            return false;
        }

        condValue = maxEig / minEig;

        invN = new double[n, n];
        for (int k = 0; k < n; k++)
        {
            double λinv = eig[k] > 1e-15 ? 1.0 / eig[k] : 0.0;
            for (int i = 0; i < n; i++)
            {
                double vik = eigVec[i, k];
                if (Math.Abs(vik) < 1e-15) continue;
                for (int j = 0; j < n; j++)
                {
                    double vjk = eigVec[j, k];
                    if (Math.Abs(vjk) < 1e-15) continue;
                    invN[i, j] += vik * λinv * vjk;
                }
            }
        }

        return true;
    }

    // ==========================================================================
    //                 NORMAL MATRIX BUILDER (3D, ПОЛНАЯ ВЕРСИЯ)
    // ==========================================================================

    private double[,] ComputeNormalMatrix(out bool isDegenerate)
    {
        isDegenerate = false;

        int nGrades = grades?.Count ?? 0;
        int nStations = selectedStations?.Count ?? 0;
        if (nGrades < 1 || nStations < 1) return null;

        // ✅ ПОЛНАЯ МАТРИЦА: станции + марки
        int n = 3 * (nStations + nGrades);
        double[,] N = new double[n, n];

        // ===== ВЕСА ИЗМЕРЕНИЙ =====
        double sigmaDist = Math.Max(1e-6, DistanceStdevMm / 1000.0);
        double wDist = 1.0 / (sigmaDist * sigmaDist);
        double sigmaAng_rad = (DirectionStdevCc / 100.0) * (Math.PI / (180.0 * 3600.0));
        double wAng = 1.0 / (sigmaAng_rad * sigmaAng_rad);

        // ===== ОБХОД СТАНЦИЙ =====
        for (int si = 0; si < nStations; si++)
        {
            var st = selectedStations[si];
            if (st?.ms60?.position == null) continue;

            Vector3 S = st.ms60.position;

            // Сбор видимых марок
            var visible = new List<(int idx, Vector3 p)>();
            for (int gi = 0; gi < nGrades; gi++)
            {
                if (grades[gi] == null) continue;
                if (HasLineOfSight(st.ms60, grades[gi]))
                    visible.Add((gi, grades[gi].position));
            }
            if (visible.Count < 1) continue;

            // === РАССТОЯНИЯ ===
            foreach (var v in visible)
            {
                Vector3 d = v.p - S;
                double dx = d.x, dy = d.y, dz = d.z;
                double r = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (r < 1e-6) continue;

                int siIdx = si * 3;
                int giIdx = (nStations + v.idx) * 3;

                double[] row = new double[n];
                row[siIdx + 0] = -dx / r; row[siIdx + 1] = -dy / r; row[siIdx + 2] = -dz / r;
                row[giIdx + 0] = dx / r; row[giIdx + 1] = dy / r; row[giIdx + 2] = dz / r;

                AccumulateRowToN(N, row, wDist);
            }

            // === УГЛЫ ===
            for (int i = 0; i < visible.Count - 1; i++)
            {
                for (int j = i + 1; j < visible.Count; j++)
                {
                    Vector3 p1 = visible[i].p - S;
                    Vector3 p2 = visible[j].p - S;

                    int siIdx = si * 3;
                    int gi1Idx = (nStations + visible[i].idx) * 3;
                    int gi2Idx = (nStations + visible[j].idx) * 3;

                    // Горизонтальный угол (плоскость XZ)
                    double h1_sq = p1.x * p1.x + p1.z * p1.z;
                    double h2_sq = p2.x * p2.x + p2.z * p2.z;
                    if (h1_sq > 1e-12 && h2_sq > 1e-12)
                    {
                        double[] rowHor = new double[n];
                        rowHor[gi1Idx + 0] = p1.z / h1_sq; rowHor[gi1Idx + 2] = -p1.x / h1_sq;
                        rowHor[gi2Idx + 0] = -p2.z / h2_sq; rowHor[gi2Idx + 2] = p2.x / h2_sq;
                        rowHor[siIdx + 0] = -rowHor[gi1Idx + 0] - rowHor[gi2Idx + 0];
                        rowHor[siIdx + 2] = -rowHor[gi1Idx + 2] - rowHor[gi2Idx + 2];
                        AccumulateRowToN(N, rowHor, wAng);
                    }

                    // Вертикальный угол
                    double r1 = Math.Sqrt(p1.x * p1.x + p1.y * p1.y + p1.z * p1.z);
                    double r2 = Math.Sqrt(p2.x * p2.x + p2.y * p2.y + p2.z * p2.z);
                    double h1 = Math.Sqrt(h1_sq);
                    double h2 = Math.Sqrt(h2_sq);
                    if (r1 > 1e-6 && r2 > 1e-6 && h1 > 1e-6 && h2 > 1e-6)
                    {
                        double[] rowVer = new double[n];
                        double denom1 = r1 * r1 * h1;
                        rowVer[gi1Idx + 0] = -(p1.x * p1.y) / denom1;
                        rowVer[gi1Idx + 1] = h1 / (r1 * r1);
                        rowVer[gi1Idx + 2] = -(p1.z * p1.y) / denom1;

                        double denom2 = r2 * r2 * h2;
                        rowVer[gi2Idx + 0] = (p2.x * p2.y) / denom2;
                        rowVer[gi2Idx + 1] = -h2 / (r2 * r2);
                        rowVer[gi2Idx + 2] = (p2.z * p2.y) / denom2;

                        rowVer[siIdx + 0] = -rowVer[gi1Idx + 0] - rowVer[gi2Idx + 0];
                        rowVer[siIdx + 1] = -rowVer[gi1Idx + 1] - rowVer[gi2Idx + 1];
                        rowVer[siIdx + 2] = -rowVer[gi1Idx + 2] - rowVer[gi2Idx + 2];

                        AccumulateRowToN(N, rowVer, wAng);
                    }
                }
            }
        }

        // ===== ФИКСАЦИЯ ДАТУМА (7 условий Хельмерта) =====
        if (nStations >= 1)
        {
            N[0, 0] += 1e12; N[1, 1] += 1e12; N[2, 2] += 1e12;
        }
        if (nStations >= 2)
        {
            N[5, 5] += 1e12; N[4, 4] += 1e12;
            Vector3 s1 = selectedStations[0].ms60.position;
            Vector3 s2 = selectedStations[1].ms60.position;
            double dx = s2.x - s1.x, dy = s2.y - s1.y, dz = s2.z - s1.z;
            double dist_sq = dx * dx + dy * dy + dz * dz;
            if (dist_sq > 1e-12)
            {
                double w_scale = 1e12 / dist_sq;
                for (int i = 0; i < 6; i++)
                    for (int j = 0; j < 6; j++)
                    {
                        double val = (i < 3 ? (j < 3 ? 1 : -1) : (j < 3 ? -1 : 1)) *
                                     new[] { dx, dy, dz, dx, dy, dz }[i] * new[] { dx, dy, dz, dx, dy, dz }[j];
                        N[i, j] += w_scale * val;
                    }
            }
        }
        if (nStations >= 3) { N[8, 8] += 1e12; }

        // Регуляризация
        const double regularization = 1e-10;
        for (int i = 0; i < n; i++) N[i, i] += regularization;

        // Проверка вырожденности
        if (!JacobiEigenDecomposition(N, out double[] ev, out _))
        {
            isDegenerate = true;
            return N;
        }

        double minEig = ev.Min();
        isDegenerate = (minEig < regularization * 0.1);

        return N;
    }

    // ==========================================================================
    //                 BUILD RAW NORMAL MATRIX (без фиксации)
    // ==========================================================================

    private double[,] BuildRawNormalMatrix()
    {
        int nGrades = grades?.Count ?? 0;
        int nStations = selectedStations?.Count ?? 0;
        if (nGrades < 1 || nStations < 1) return null;

        int n = 3 * (nStations + nGrades);
        double[,] N = new double[n, n];

        double sigmaDist = Math.Max(1e-6, DistanceStdevMm / 1000.0);
        double wDist = 1.0 / (sigmaDist * sigmaDist);
        double sigmaAng_rad = (DirectionStdevCc / 100.0) * (Math.PI / (180.0 * 3600.0));
        double wAng = 1.0 / (sigmaAng_rad * sigmaAng_rad);

        for (int si = 0; si < nStations; si++)
        {
            var st = selectedStations[si];
            if (st?.ms60?.position == null) continue;

            Vector3 S = st.ms60.position;

            var visible = new List<(int idx, Vector3 p)>();
            for (int gi = 0; gi < nGrades; gi++)
            {
                if (grades[gi] == null) continue;
                if (HasLineOfSight(st.ms60, grades[gi]))
                    visible.Add((gi, grades[gi].position));
            }
            if (visible.Count < 1) continue;

            foreach (var v in visible)
            {
                Vector3 d = v.p - S;
                double dx = d.x, dy = d.y, dz = d.z;
                double r = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (r < 1e-6) continue;

                int siIdx = si * 3;
                int giIdx = (nStations + v.idx) * 3;

                double[] row = new double[n];
                row[siIdx + 0] = -dx / r; row[siIdx + 1] = -dy / r; row[siIdx + 2] = -dz / r;
                row[giIdx + 0] = dx / r; row[giIdx + 1] = dy / r; row[giIdx + 2] = dz / r;

                AccumulateRowToN(N, row, wDist);
            }

            for (int i = 0; i < visible.Count - 1; i++)
            {
                for (int j = i + 1; j < visible.Count; j++)
                {
                    Vector3 p1 = visible[i].p - S;
                    Vector3 p2 = visible[j].p - S;

                    int siIdx = si * 3;
                    int gi1Idx = (nStations + visible[i].idx) * 3;
                    int gi2Idx = (nStations + visible[j].idx) * 3;

                    double h1_sq = p1.x * p1.x + p1.z * p1.z;
                    double h2_sq = p2.x * p2.x + p2.z * p2.z;
                    if (h1_sq > 1e-12 && h2_sq > 1e-12)
                    {
                        double[] rowHor = new double[n];
                        rowHor[gi1Idx + 0] = p1.z / h1_sq; rowHor[gi1Idx + 2] = -p1.x / h1_sq;
                        rowHor[gi2Idx + 0] = -p2.z / h2_sq; rowHor[gi2Idx + 2] = p2.x / h2_sq;
                        rowHor[siIdx + 0] = -rowHor[gi1Idx + 0] - rowHor[gi2Idx + 0];
                        rowHor[siIdx + 2] = -rowHor[gi1Idx + 2] - rowHor[gi2Idx + 2];
                        AccumulateRowToN(N, rowHor, wAng);
                    }

                    double r1 = Math.Sqrt(p1.x * p1.x + p1.y * p1.y + p1.z * p1.z);
                    double r2 = Math.Sqrt(p2.x * p2.x + p2.y * p2.y + p2.z * p2.z);
                    double h1 = Math.Sqrt(h1_sq);
                    double h2 = Math.Sqrt(h2_sq);
                    if (r1 > 1e-6 && r2 > 1e-6 && h1 > 1e-6 && h2 > 1e-6)
                    {
                        double[] rowVer = new double[n];
                        double denom1 = r1 * r1 * h1;
                        rowVer[gi1Idx + 0] = -(p1.x * p1.y) / denom1;
                        rowVer[gi1Idx + 1] = h1 / (r1 * r1);
                        rowVer[gi1Idx + 2] = -(p1.z * p1.y) / denom1;

                        double denom2 = r2 * r2 * h2;
                        rowVer[gi2Idx + 0] = (p2.x * p2.y) / denom2;
                        rowVer[gi2Idx + 1] = -h2 / (r2 * r2);
                        rowVer[gi2Idx + 2] = (p2.z * p2.y) / denom2;

                        rowVer[siIdx + 0] = -rowVer[gi1Idx + 0] - rowVer[gi2Idx + 0];
                        rowVer[siIdx + 1] = -rowVer[gi1Idx + 1] - rowVer[gi2Idx + 1];
                        rowVer[siIdx + 2] = -rowVer[gi1Idx + 2] - rowVer[gi2Idx + 2];

                        AccumulateRowToN(N, rowVer, wAng);
                    }
                }
            }
        }

        return N;
    }
    // ==========================================================================
    //                         UTILITY: ACCUMULATOR
    // ==========================================================================

    private void AccumulateRowToN(double[,] N, double[] row, double weight)
    {
        int n = N.GetLength(0);

        for (int i = 0; i < n; i++)
        {
            double ri = row[i];
            if (Math.Abs(ri) < 1e-16) continue;

            for (int j = 0; j < n; j++)
            {
                double rj = row[j];
                if (Math.Abs(rj) < 1e-16) continue;

                N[i, j] += weight * ri * rj;
            }
        }
    }

    // ==========================================================================
    //                JACOBI EIGEN DECOMPOSITION (3D, SYMMETRIC)
    // ==========================================================================

    private bool JacobiEigenDecomposition(double[,] A, out double[] eval, out double[,] evec, int maxIter = 200)
    {
        int n = A.GetLength(0);

        eval = new double[n];
        evec = new double[n, n];

        double[,] D = new double[n, n];

        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
                D[i, j] = A[i, j];

            for (int j = 0; j < n; j++)
                evec[i, j] = (i == j ? 1 : 0);
        }

        for (int iter = 0; iter < maxIter; iter++)
        {
            double maxVal = 0;
            int p = 0, q = 1;

            for (int i = 0; i < n; i++)
            {
                for (int j = i + 1; j < n; j++)
                {
                    double v = Math.Abs(D[i, j]);
                    if (v > maxVal)
                    {
                        p = i;
                        q = j;
                        maxVal = v;
                    }
                }
            }

            if (maxVal < 1e-12) break;

            double phi = 0.5 * Math.Atan2(2 * D[p, q], D[q, q] - D[p, p]);
            double c = Math.Cos(phi);
            double s = Math.Sin(phi);

            double Dpp = c * c * D[p, p] - 2 * s * c * D[p, q] + s * s * D[q, q];
            double Dqq = s * s * D[p, p] + 2 * s * c * D[p, q] + c * c * D[q, q];

            D[p, p] = Dpp;
            D[q, q] = Dqq;
            D[p, q] = D[q, p] = 0;

            for (int i = 0; i < n; i++)
            {
                if (i != p && i != q)
                {
                    double dip = c * D[i, p] - s * D[i, q];
                    double diq = s * D[i, p] + c * D[i, q];
                    D[i, p] = D[p, i] = dip;
                    D[i, q] = D[q, i] = diq;
                }

                double vip = c * evec[i, p] - s * evec[i, q];
                double viq = s * evec[i, p] + c * evec[i, q];
                evec[i, p] = vip;
                evec[i, q] = viq;
            }
        }

        for (int i = 0; i < n; i++)
            eval[i] = D[i, i];

        return true;
    }

    // ==========================================================================
    //                         GEOMETRY ANALYSIS
    // ==========================================================================

    private void AnalyzeGeometry()
    {
        var covered = new HashSet<Transform>();
        foreach (var st in selectedStations)
            if (st.visibleGrades != null)
                covered.UnionWith(st.visibleGrades);

        Debug.Log($"Покрытие: {covered.Count}/{grades.Count}");

        int good = 0, bad = 0;
        foreach (var g in grades)
        {
            var stations = selectedStations
                .Where(s => s.visibleGrades != null && s.visibleGrades.Contains(g))
                .ToList();

            if (stations.Count < 2) continue;

            bool ok = false;
            for (int i = 0; i < stations.Count - 1 && !ok; i++)
            {
                for (int j = i + 1; j < stations.Count; j++)
                {
                    float angle = Vector3.Angle(
                        stations[i].position - g.position,
                        stations[j].position - g.position);

                    if (angle >= 30 && angle <= 150)
                    {
                        ok = true;
                        break;
                    }
                }
            }
            if (ok) good++; else bad++;
        }

        Debug.Log($"Хорошая геометрия (30°-150°): {good}");
        Debug.Log($"Плохая геометрия: {bad}");
    }
    private bool ComputeConditionNumberSVD(out double condValue, out double[,] invN)
    {
        condValue = double.PositiveInfinity;
        invN = null;

        // Используем 3D-версию матрицы с параметром out double
        double quality;
        double[,] N = ComputeNormalMatrix(out bool isDegenerate);
        if (N == null || isDegenerate) return false;

        int n = N.GetLength(0);

        // Якоби-декомпозиция вместо SVD из MathNet
        if (!JacobiEigenDecomposition(N, out double[] eig, out double[,] eigVec))
        {
            Debug.LogWarning("Eigen decomposition failed.");
            return false;
        }

        double minEig = double.MaxValue;
        double maxEig = double.MinValue;

        for (int i = 0; i < eig.Length; i++)
        {
            if (eig[i] < minEig) minEig = eig[i];
            if (eig[i] > maxEig) maxEig = eig[i];
        }

        if (minEig <= 1e-15)
        {
            Debug.LogWarning("Минимальное собственное значение ~ 0 — сеть вырождена.");
            return false;
        }

        condValue = maxEig / minEig;

        // Псевдообратная матрица: N⁻¹ = V * Λ⁻¹ * Vᵀ
        invN = new double[n, n];

        for (int k = 0; k < eig.Length; k++)
        {
            double λinv = eig[k] > 1e-15 ? 1.0 / eig[k] : 0.0;

            for (int i = 0; i < n; i++)
            {
                double vik = eigVec[i, k];
                if (Math.Abs(vik) < 1e-15) continue;

                for (int j = 0; j < n; j++)
                {
                    double vjk = eigVec[j, k];
                    if (Math.Abs(vjk) < 1e-15) continue;

                    invN[i, j] += vik * λinv * vjk;
                }
            }
        }

        return true;
    }
    private List<(Vector3 pos, double cond)> SuggestStationPositions(int topK = 3)
    {
        var suggestions = new List<(Vector3 pos, double cond)>();

        // Упрощенная версия - возвращаем пустой список
        // В реальной реализации здесь можно добавить логику для предложения новых позиций
        Debug.Log("Функция предложения позиций станций не реализована в упрощенной версии");
        return suggestions;
    }

    private void RunPython()
    {
        // Если поле пустое, используем путь по умолчанию
        string pythonFile = string.IsNullOrEmpty(pythonFilePath)
            ? "C:/VR/My projectX/Assets/Scenes/GNU/run_gama.py"
            : pythonFilePath;

        try
        {
            string pyPath = "";

            // 1. Проверяем указанный путь
            if (File.Exists(pythonFile))
            {
                pyPath = pythonFile;
                Debug.Log($"✓ Python файл найден: {pyPath}");
            }
            else
            {
                // 2. Пробуем разные варианты
                string[] possiblePaths =
                {
                pythonFile, // Как указано
                Path.Combine(Application.dataPath, "Scenes/GNU/run_gama.py"),
                Path.Combine(Application.dataPath, "../Scenes/GNU/run_gama.py"),
                "C:/VR/My projectX/Assets/Scenes/GNU/run_gama.py",
                Path.Combine(Directory.GetCurrentDirectory(), "Assets/Scenes/GNU/run_gama.py")
            };

                foreach (string path in possiblePaths)
                {
                    if (File.Exists(path))
                    {
                        pyPath = path;
                        Debug.Log($"✓ Python файл найден: {pyPath}");
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(pyPath))
            {
                if (resultText != null)
                {
                    resultText.text = $"[ERROR] Не найден Python файл!\n";
                    resultText.text += $"Искали: {pythonFile}\n";
                    resultText.text += $"Data Path: {Application.dataPath}\n";
                    resultText.text += $"Текущая папка: {Directory.GetCurrentDirectory()}\n";
                }
                return;
            }

            // Определяем Python
            string pythonCmd = "";
            string[] pythonCommands = { "python", "python3", "py" };

            foreach (string cmd in pythonCommands)
            {
                try
                {
                    var testPsi = new ProcessStartInfo
                    {
                        FileName = cmd,
                        Arguments = "--version",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    };

                    using (var testProc = Process.Start(testPsi))
                    {
                        if (testProc != null)
                        {
                            testProc.WaitForExit(1000);
                            if (testProc.ExitCode == 0)
                            {
                                pythonCmd = cmd;
                                Debug.Log($"✓ Python найден: {cmd}");
                                break;
                            }
                        }
                    }
                }
                catch { }
            }

            if (string.IsNullOrEmpty(pythonCmd))
            {
                if (resultText != null)
                    resultText.text += "[ERROR] Python не установлен!\n";
                return;
            }

            // Запускаем Python
            var psi = new ProcessStartInfo
            {
                FileName = pythonCmd,
                Arguments = $"\"{pyPath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(pyPath)
            };

            if (resultText != null)
                resultText.text = $"[INFO] Запуск: {pythonCmd} \"{pyPath}\"\n";

            using (var proc = Process.Start(psi))
            {
                string output = proc.StandardOutput.ReadToEnd();
                string error = proc.StandardError.ReadToEnd();
                proc.WaitForExit();

                if (!string.IsNullOrEmpty(error) && resultText != null)
                {
                    resultText.text += "[ERROR]\n" + error + "\n";
                }

                if (!string.IsNullOrEmpty(output))
                {
                    try
                    {
                        var result = JsonUtility.FromJson<PythonResult>(output);

                        if (resultText != null)
                        {
                            resultText.text += $"[PYTHON STATUS] {result.status}\n";

                            if (result.errors != null && result.errors.Length > 0)
                                resultText.text += "[ERRORS]\n" + string.Join("\n", result.errors) + "\n";

                            if (result.outputs != null && !string.IsNullOrEmpty(result.outputs.report))
                                resultText.text += "[REPORT]\n" + result.outputs.report + "\n";
                        }
                    }
                    catch
                    {
                        if (resultText != null)
                            resultText.text += output + "\n";
                    }
                }
            }

            // Проверяем выходные файлы
            string outputDir = Path.Combine(Application.dataPath, "StreamingAssets");
            string[] expectedFiles = { "result.svg", "result.txt", "result.xml", "result.html" };

            foreach (var file in expectedFiles)
            {
                string path = Path.Combine(outputDir, file);
                if (File.Exists(path))
                {
                    if (resultText != null)
                        resultText.text += $"[SAVED] {file}\n";
                }
                else
                {
                    if (resultText != null)
                        resultText.text += $"[MISSING] {file}\n";
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"Python error: {e}");
            if (resultText != null)
                resultText.text += $"[EXCEPTION] {e.Message}\n";
        }
    }

    public void PrintPointAposterioriFromUnity()
    {
        if (selectedStations.Count == 0 || grades.Count == 0)
        {
            Debug.LogWarning("Станции или марки отсутствуют.");
            return;
        }

        foreach (var g in grades)
        {
            var visibleStations = selectedStations
                .Where(s => s.visibleGrades.Contains(g))
                .ToList();

            if (visibleStations.Count == 0)
            {
                Debug.Log($"Марка {g.name}: недоступна для измерения (не видна ни одной станцией).");
                continue;
            }

            Station nearest = visibleStations.OrderBy(s => Vector3.Distance(s.ms60.position, g.position)).First();
            float dist = Vector3.Distance(nearest.ms60.position, g.position);

            float sx = dist * 0.001f;
            float sy = dist * 0.001f;
            float sp = Mathf.Sqrt(sx * sx + sy * sy);

            Debug.Log($"Марка {g.name}: σX={sx:F4}, σY={sy:F4}, σp={sp:F4}");
        }
    }

    public void ClearStations()
    {
        selectedStations.Clear();

        // Очистка визуализации
        foreach (Transform t in parentContainer)
        {
            if (t.name == "Line" || t.name.StartsWith("CandidateCond_"))
                Destroy(t.gameObject);
        }

        lastSuggestions?.Clear();
        Debug.Log("Станции и визуализация очищены.");
    }
}
