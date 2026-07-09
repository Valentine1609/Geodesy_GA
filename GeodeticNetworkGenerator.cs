using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using TMPro;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;



public class GeodeticNetworkGenerator : MonoBehaviour
{
    [Header("Настройки")]
    public GameObject networkPrefab;
    public TMP_Dropdown buildingDropdown;
    public float offset = 50f;
    public Transform parentContainer;
    [Header("Raycast")]
    public float raycastHeight = 5f;
    public LayerMask groundLayer;
    public LayerMask obstacleLayer;
    [Header("Ограничения количества станций")]
    public int minStations = 4;
    [Header("py файл (указываем через Inspector)")]
    [Header("СКО")]
    public TMP_InputField directionStdevInput;
    [Header("UI")]
    public TMP_Text resultText;
    public TMP_Text networkReportUIText;
    [Header("UI")]
    public TMP_InputField distanceStdevInput;
    [Header("Шум координат (мм)")]
    public TMP_InputField coordNoiseInput;
    public string pythonFilePath;
    [Header("Геометрическая устойчивость")]
    public bool showWeakLinks = true;       // показывать слабые позиции
    public float gizmoSphereSize = 0.3f;    // размер сфер для кандидатов
    public float condScale = 1000f;         // масштаб для нормализации цвета
    private List<(Vector3 pos, double cond)> lastSuggestions;  // сохраняем последние кандидаты


    private GameObject targetBuilding;

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

    private void Start()
    {
        PopulateBuildingDropdown();
        if (buildingDropdown != null)
        {
            buildingDropdown.onValueChanged.AddListener(_ => UpdateTargetBuilding());
            UpdateTargetBuilding();
        }
    }

    private void PopulateBuildingDropdown()
    {
        if (buildingDropdown == null) return;
        var buildings = GameObject.FindGameObjectsWithTag("building");
        buildingDropdown.ClearOptions();
        buildingDropdown.AddOptions(buildings.Select(b => b.name).ToList());
    }

    private void UpdateTargetBuilding()
    {
        if (buildingDropdown == null || buildingDropdown.options.Count == 0)
        {
            targetBuilding = null;
            return;
        }
        string buildingName = buildingDropdown.options[buildingDropdown.value].text;
        targetBuilding = GameObject.Find(buildingName);
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
            // Логируем кандидатов
            Debug.Log($"Кандидат: {s.pos}, cond = {s.cond:F2}");

            // Визуально создаём сферу в сцене
            if (showWeakLinks)
            {
                GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                sphere.transform.position = s.pos + Vector3.up * 0.2f;
                sphere.transform.localScale = Vector3.one * gizmoSphereSize;

                // Цвет: чем выше число обусловленности, тем краснее
                float t = Mathf.Clamp01((float)((s.cond - 1.0) / condScale));
                sphere.GetComponent<Renderer>().material.color = Color.Lerp(Color.green, Color.red, t);

                // Не мешаем физике
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
        PrintNetworkReport();
        ExportStationsToCSV();
        //PerformAdjustmentUsingExistingMatrix();
        //ComputeAndPrintAdjustedObservations();
    }

    private void GenerateNetwork()
    {
        if (networkPrefab == null || targetBuilding == null)
        {
            Debug.LogError("Нет префаба тахеометра или здания");
            return;
        }

        // ✅ ОТЛАДКА
        Debug.Log($"[DEBUG] networkPrefab={networkPrefab != null}, " +
                 $"targetBuilding={targetBuilding != null}, " +
                 $"groundLayer={groundLayer.value}");

        // Очистка
        foreach (Transform t in parentContainer) Destroy(t.gameObject);
        selectedStations.Clear();

        // Сбор марок
        grades = targetBuilding.GetComponentsInChildren<Transform>()
            .Where(t => t.CompareTag("grade") && t.parent.CompareTag("building"))
            .ToList();

        Debug.Log($"[DEBUG] Найдено марок: {grades.Count}");

        if (grades.Count == 0)
        {
            Debug.LogWarning("Нет марок на здании");
            return;
        }
        if (networkPrefab == null || targetBuilding == null)
        {
            Debug.LogError("Нет префаба тахеометра или здания");
            return;
        }

        // Очистка
        foreach (Transform t in parentContainer) Destroy(t.gameObject);
        selectedStations.Clear();

        // Сбор марок
        grades = targetBuilding.GetComponentsInChildren<Transform>()
            .Where(t => t.CompareTag("grade") && t.parent.CompareTag("building"))
            .ToList();

        if (grades.Count == 0)
        {
            Debug.LogWarning("Нет марок на здании");
            return;
        }

        // Генетический алгоритм для поиска оптимальной конфигурации станций
        List<Station> bestSolution = RunGeneticAlgorithm();

        // Применяем найденное решение
        foreach (var station in bestSolution)
        {
            TryPlaceStation(station, targetBuilding.GetComponent<Collider>());
        }

        Debug.Log($"Генетический алгоритм завершен. Размещено станций: {selectedStations.Count}");
        DebugStationCoverage(bestSolution);
    }

    private Dictionary<Vector3Int, HashSet<Transform>> visibilityCache = new Dictionary<Vector3Int, HashSet<Transform>>();


    // Основной метод генетического алгоритма
    private List<Station> RunGeneticAlgorithm()
    {
        // Очищаем кэш перед началом
        if (visibilityCache != null)
            visibilityCache.Clear();

        // ЗАЩИТА: Проверяем, что есть марки
        if (grades == null || grades.Count == 0)
        {
            Debug.LogError("Нет марок для генерации сети!");
            return new List<Station>();
        }

        Bounds bounds = GetObjectBounds(targetBuilding);
        Collider buildingCollider = targetBuilding.GetComponent<Collider>();

        if (buildingCollider == null)
        {
            Debug.LogError("Collider здания не найден!");
            return new List<Station>();
        }

        // АДАПТИВНЫЕ ПАРАМЕТРЫ В ЗАВИСИМОСТИ ОТ КОЛИЧЕСТВА МАРОК
        // ЗАЩИТА ОТ ДЕЛЕНИЯ НА НОЛЬ: используем Mathf.Max(1, grades.Count)
        float stationCountFactor = Mathf.Clamp(Mathf.Max(1, grades.Count) / 10f, 0.5f, 3f);

        // ЗАЩИТА: убеждаемся, что stationCountFactor не равен нулю
        if (stationCountFactor <= 0) stationCountFactor = 1f;

        int populationSize = Mathf.Clamp(grades.Count * 4, 40, 90); // расширенная популяция для поиска станций с обеих сторон
        int generations = Mathf.Clamp(grades.Count * 8, 80, 180);   // больше поколений для замкнутого хода и хорошей обусловленности
        float mutationRate = 0.35f; // ниже стартовая мутация: меньше ломает найденные двухсторонние решения
        float crossoverRate = 0.85f; // высокий шанс кроссовера для комбинирования сторон здания
        int eliteCount = Mathf.Clamp(populationSize / 6, 5, 12); // больше элиты, чтобы сохранять удачные ходы

        // ЗАЩИТА: проверяем, что eliteCount не отрицательный
        eliteCount = Mathf.Max(2, eliteCount);

        float baseOffset = CalculateOptimalDistance(bounds);

        // Генерация кандидатных позиций
        var candidatePositions = GenerateCandidatePositions(bounds, buildingCollider, baseOffset);

        // ЗАЩИТА: проверяем, что есть кандидатные позиции
        if (candidatePositions == null || candidatePositions.Count == 0)
        {
            Debug.LogError("Не удалось сгенерировать валидные кандидатные позиции!");
            return GenerateFallbackSolution(bounds, buildingCollider, baseOffset);
        }

        // ОГРАНИЧИВАЕМ количество кандидатов при большом количестве марок
        if (candidatePositions.Count > 150)
        {
            candidatePositions = SelectBalancedCandidatesByGradeSides(candidatePositions, bounds, 150);
            Debug.Log($"Ограничено количество кандидатов до {candidatePositions.Count} с балансом по сторонам здания");
        }

        Debug.Log($"Запуск ГА: {grades.Count} марок, кандидатов={candidatePositions.Count}, " +
                  $"популяция={populationSize}, поколений={generations}");

        // Ограничиваем максимальное время выполнения
        System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
        float maxTimeSeconds = Mathf.Min(Mathf.Max(1, grades.Count) * 1.0f + 30f, 120f); // Макс 30 секунд

        // Генерация начальной популяции
        List<List<Station>> population;
        try
        {
            population = GenerateInitialPopulation(populationSize, candidatePositions);
        }
        catch (Exception ex)
        {
            Debug.LogError($"Ошибка при генерации начальной популяции: {ex.Message}");
            return GenerateFallbackSolution(bounds, buildingCollider, baseOffset);
        }

        // ЗАЩИТА: проверяем, что популяция создана
        if (population == null || population.Count == 0)
        {
            Debug.LogError("Не удалось создать начальную популяцию!");
            return GenerateFallbackSolution(bounds, buildingCollider, baseOffset);
        }

        List<Station> bestSolution = null;
        float bestFitness = float.MinValue;
        int generationsWithoutImprovement = 0;
        int maxGenerationsWithoutImprovement = 30;

        // Переменные для отслеживания прогресса
        bool foundFullCoverage = false;
        int lastLogGeneration = -10;

        for (int generation = 0; generation < generations; generation++)
        {
            // ПРОВЕРКА ТАЙМАУТА
            if (stopwatch.Elapsed.TotalSeconds > maxTimeSeconds)
            {
                Debug.Log($"ГА прерван по времени: {stopwatch.Elapsed.TotalSeconds:F1}с");
                break;
            }

            var fitnessScores = new List<(List<Station> solution, float fitness)>();

            // ОЦЕНКА ПРИСПОСОБЛЕННОСТИ
            foreach (var solution in population)
            {
                // ЗАЩИТА: проверяем solution на null
                if (solution == null) continue;

                float fitness = CalculateFitness(solution, bounds, buildingCollider);
                fitnessScores.Add((solution, fitness));

                // Проверяем полное покрытие
                var coveredGrades = new HashSet<Transform>();
                foreach (var station in solution)
                {
                    if (station == null) continue;
                    if (station.visibleGrades == null) continue;

                    foreach (var grade in station.visibleGrades)
                    {
                        coveredGrades.Add(grade);
                    }
                }

                int redundantMarks = CountMarksWithAtLeastTwoObservers(solution);
                int acuteMarks = CountMarksWithAcuteAngles(solution);

                if (coveredGrades.Count == grades.Count && redundantMarks == grades.Count && acuteMarks == 0 && HasClosedTraverse(solution, bounds))
                {
                    foundFullCoverage = true;

                    // Обновляем лучшее решение
                    if (fitness > bestFitness)
                    {
                        bestFitness = fitness;
                        bestSolution = solution.ToList();
                        generationsWithoutImprovement = 0;

                        // Логируем улучшение
                        if (generation - lastLogGeneration >= 5 || generation % 10 == 0)
                        {
                            lastLogGeneration = generation;
                            Debug.Log($"✅ Поколение {generation}: Полное покрытие + x2 наблюдения + без острых углов + замкнутый ход. Фитнес={bestFitness:F0}, " +
                                     $"Станций={bestSolution.Count}");
                        }

                        // РАННИЙ ВЫХОД если нашли хорошее компактное решение
                        if (bestSolution.Count <= 8 && fitness > 1000000f)
                        {
                            Debug.Log($"🎯 Ранний выход: найдено оптимальное решение на поколении {generation}");
                            stopwatch.Stop();
                            return FinalizeNetworkQuality(
                                RefineSolutionForRedundancyAndAngles(bestSolution, candidatePositions, bounds, buildingCollider),
                                candidatePositions,
                                bounds);
                        }
                    }
                }
                else
                {
                    // Обновляем лучшее решение даже без полного покрытия
                    if (fitness > bestFitness)
                    {
                        bestFitness = fitness;
                        bestSolution = solution.ToList();
                        generationsWithoutImprovement = 0;

                        // Логируем только каждые 10 поколений
                        if (generation % 10 == 0)
                        {
                            Debug.Log($"🔄 Поколение {generation}: Новый лучший фитнес={bestFitness:F0}, " +
                                     $"Покрытие={coveredGrades.Count}/{grades.Count}");
                        }
                    }
                }
            }

            // ЗАЩИТА: проверяем, что есть результаты фитнеса
            if (fitnessScores.Count == 0)
            {
                Debug.LogWarning($"Поколение {generation}: нет валидных решений в популяции");
                break;
            }

            // Проверяем условия остановки
            if (foundFullCoverage && generation > 15 && generationsWithoutImprovement > 5)
            {
                Debug.Log($"⏹️ Остановка: найдено полное покрытие и нет улучшений {generationsWithoutImprovement} поколений");
                break;
            }

            generationsWithoutImprovement++;
            if (generationsWithoutImprovement >= maxGenerationsWithoutImprovement)
            {
                Debug.Log($"⏹️ Остановка: нет улучшений {generationsWithoutImprovement} поколений");
                break;
            }

            // Адаптивная мутация
            if (generationsWithoutImprovement > 10)
            {
                float adaptiveMutationRate = Mathf.Min(0.4f, mutationRate * 1.3f);
                if (adaptiveMutationRate != mutationRate)
                {
                    mutationRate = adaptiveMutationRate;
                    if (generation % 5 == 0)
                        Debug.Log($"🔧 Адаптивная мутация: коэффициент увеличен до {mutationRate:F2}");
                }
            }

            // СОРТИРОВКА И ЭЛИТИЗМ
            fitnessScores.Sort((a, b) => b.fitness.CompareTo(a.fitness));
            var newPopulation = new List<List<Station>>();

            // Сохраняем элиту
            for (int i = 0; i < eliteCount && i < fitnessScores.Count; i++)
            {
                var eliteSolution = fitnessScores[i].solution;
                var newElite = new List<Station>();
                foreach (var station in eliteSolution)
                {
                    if (station == null) continue;
                    newElite.Add(new Station
                    {
                        position = station.position,
                        visibleGrades = station.visibleGrades != null
                            ? new HashSet<Transform>(station.visibleGrades)
                            : new HashSet<Transform>()
                    });
                }
                if (newElite.Count > 0)
                    newPopulation.Add(newElite);
            }

            int attempts = 0;
            while (newPopulation.Count < populationSize && attempts < populationSize * 2)
            {
                attempts++;

                // Используем настраиваемую вероятность кроссовера, чтобы чаще комбинировать удачные стороны здания.
                List<Station> offspring;
                if (UnityEngine.Random.value < crossoverRate && fitnessScores.Count >= 2)
                {
                    // КРОССОВЕР двух родителей
                    var parent1 = TournamentSelection(fitnessScores, 3);
                    var parent2 = TournamentSelection(fitnessScores, 3);
                    offspring = Crossover(parent1, parent2, bounds, buildingCollider);
                }
                else
                {
                    // КЛОНИРОВАНИЕ одного родителя
                    var parent = TournamentSelection(fitnessScores, 3);
                    offspring = parent.Select(s => new Station
                    {
                        position = s.position,
                        visibleGrades = new HashSet<Transform>(s.visibleGrades)
                    }).ToList();
                }

                // МУТАЦИЯ потомка
                if (offspring != null && offspring.Count > 0 && UnityEngine.Random.value < mutationRate)
                {
                    offspring = Mutate(offspring, 0.5f, bounds, buildingCollider, baseOffset); // 50% шанс мутации
                }

                // БАЗОВАЯ ПРОВЕРКА: потомок должен быть валидным
                if (offspring != null && offspring.Count >= minStations)
                {
                    // Проверяем, что все станции видят хотя бы одну марку
                    bool allValid = true;
                    foreach (var station in offspring)
                    {
                        if (station.visibleGrades == null || station.visibleGrades.Count == 0)
                        {
                            allValid = false;
                            break;
                        }
                    }

                    if (allValid)
                    {
                        newPopulation.Add(offspring);
                    }
                }
            }
            // Если не удалось заполнить популяцию, добавляем случайные решения
            while (newPopulation.Count < populationSize)
            {
                int stationCount = UnityEngine.Random.Range(minStations, Mathf.Min(8, candidatePositions.Count));
                var randomSolution = new List<Station>();

                var shuffled = candidatePositions.OrderBy(x => UnityEngine.Random.value).ToList();
                for (int i = 0; i < stationCount && i < shuffled.Count; i++)
                {
                    var visible = GetVisibleGradesFromPos(shuffled[i]);
                    if (visible.Count > 0)
                    {
                        randomSolution.Add(new Station
                        {
                            position = shuffled[i],
                            visibleGrades = visible
                        });
                    }
                }

                if (randomSolution.Count >= minStations)
                {
                    newPopulation.Add(randomSolution);
                }
                else
                {
                    break; // Не получается создать решение
                }
            }

            population = newPopulation;

            // ЛОГИРОВАНИЕ ПРОГРЕССА (только каждые 5-10 поколений)
            if (generation % 10 == 0 || (generation < 10 && generation % 2 == 0))
            {
                var currentBest = fitnessScores[0].solution;
                var covered = new HashSet<Transform>();
                if (currentBest != null)
                {
                    foreach (var station in currentBest)
                    {
                        if (station == null || station.visibleGrades == null) continue;
                        foreach (var grade in station.visibleGrades)
                        {
                            covered.Add(grade);
                        }
                    }
                }

                // Анализ расстояний
                float avgDistance = 0f;
                int validStations = 0;
                if (currentBest != null)
                {
                    foreach (var station in currentBest)
                    {
                        if (station == null) continue;
                        avgDistance += Vector3.Distance(station.position, bounds.center);
                        validStations++;
                    }
                    if (validStations > 0) avgDistance /= validStations;
                }

                Debug.Log($"📊 Поколение {generation}: " +
                         $"Лучший фитнес={fitnessScores[0].fitness:F0}, " +
                         $"Покрытие={covered.Count}/{grades.Count}, " +
                         $"Станций={validStations}, " +
                         $"Ср.расст={avgDistance:F1}m, " +
                         $"Время={stopwatch.Elapsed.TotalSeconds:F1}с");
            }
        }

        stopwatch.Stop();
        Debug.Log($"ГА завершен за {stopwatch.Elapsed.TotalSeconds:F1}с");

        // ФИНАЛЬНАЯ ВАЛИДАЦИЯ И ВОЗВРАТ
        if (bestSolution != null && bestSolution.Count > 0)
        {
            // Проверяем покрытие
            var finalCovered = new HashSet<Transform>();
            int stationsInsideBuilding = 0;
            int validStations = 0;

            foreach (var station in bestSolution)
            {
                if (station == null) continue;
                validStations++;

                // Пересчитываем видимость для актуальности
                station.visibleGrades = GetVisibleGradesFromPos(station.position);
                if (station.visibleGrades != null)
                {
                    foreach (var grade in station.visibleGrades)
                    {
                        finalCovered.Add(grade);
                    }
                }

                if (IsInsideAnyBuilding(station.position))
                {
                    stationsInsideBuilding++;
                }
            }

            // Отчет о результате
            if (stationsInsideBuilding > 0)
            {
                Debug.LogError($"❌ {stationsInsideBuilding} станций внутри здания! Используем fallback.");
                return GenerateFallbackSolution(bounds, buildingCollider, baseOffset);
            }

            if (validStations == 0)
            {
                Debug.LogError("❌ Нет валидных станций в решении!");
                return GenerateFallbackSolution(bounds, buildingCollider, baseOffset);
            }

            if (finalCovered.Count == grades.Count)
            {
                Debug.Log($"=== РЕЗУЛЬТАТ ГА ===");
                Debug.Log($"Лучшее решение: {validStations} станций");
                Debug.Log($"Покрыто марок: {finalCovered.Count}/{grades.Count} ({(float)finalCovered.Count / grades.Count * 100:F1}%)");
                Debug.Log($"Фитнес: {bestFitness:F0}");
                Debug.Log($"Время выполнения: {stopwatch.Elapsed.TotalSeconds:F1}с");
                Debug.Log($"🎉 УСПЕХ: Все {grades.Count} марок покрыты! " +
                         $"Станций={validStations}, Время={stopwatch.Elapsed.TotalSeconds:F1}с");
            }
            else
            {
                Debug.LogWarning($"⚠️ Частичный успех: {finalCovered.Count}/{grades.Count} марок, " +
                               $"Станций={validStations}");

                // Если покрытие недостаточное, пробуем улучшить через fallback
                if (finalCovered.Count < grades.Count * 0.8f) // Меньше 80% покрытия
                {
                    Debug.Log("Попытка улучшить через fallback...");
                    var fallback = GenerateFallbackSolution(bounds, buildingCollider, baseOffset);

                    // Выбираем лучшее решение
                    float bestFitnessVal = CalculateFitness(bestSolution, bounds, buildingCollider);
                    float fallbackFitness = CalculateFitness(fallback, bounds, buildingCollider);

                    var candidateBest = fallbackFitness > bestFitnessVal ? fallback : bestSolution;
                    return FinalizeNetworkQuality(
                        RefineSolutionForRedundancyAndAngles(candidateBest, candidatePositions, bounds, buildingCollider),
                        candidatePositions,
                        bounds);
                }
            }

            return FinalizeNetworkQuality(
                RefineSolutionForRedundancyAndAngles(bestSolution, candidatePositions, bounds, buildingCollider),
                candidatePositions,
                bounds);
        }
        else
        {
            Debug.LogWarning("ГА не нашел решения, используем fallback");
            var fallback = GenerateFallbackSolution(bounds, buildingCollider, baseOffset);
            return FinalizeNetworkQuality(
                RefineSolutionForRedundancyAndAngles(fallback, candidatePositions, bounds, buildingCollider),
                candidatePositions,
                bounds);
        }
    }

    private List<Station> RefineSolutionForRedundancyAndAngles(
        List<Station> solution,
        List<Vector3> candidatePositions,
        Bounds bounds,
        Collider buildingCollider)
    {
        if (solution == null)
            return new List<Station>();

        var refined = solution
            .Where(s => s != null)
            .Select(s => new Station
            {
                position = s.position,
                visibleGrades = s.visibleGrades != null ? new HashSet<Transform>(s.visibleGrades) : GetVisibleGradesFromPos(s.position)
            })
            .ToList();

        if (candidatePositions == null || candidatePositions.Count == 0)
            return PruneRedundantStations(refined);

        const int maxExtraStations = 4;
        const float minSpacing = 6f;

        for (int step = 0; step < maxExtraStations; step++)
        {
            foreach (var st in refined)
                st.visibleGrades = GetVisibleGradesFromPos(st.position);

            int redundantMarks = CountMarksWithAtLeastTwoObservers(refined);
            int acuteMarks = CountMarksWithAcuteAngles(refined);
            if (redundantMarks == grades.Count && acuteMarks == 0)
                break;

            var observationCounts = new Dictionary<Transform, int>();
            foreach (var g in grades)
                observationCounts[g] = 0;

            foreach (var st in refined)
            {
                if (st.visibleGrades == null) continue;
                foreach (var g in st.visibleGrades)
                {
                    if (observationCounts.ContainsKey(g))
                        observationCounts[g]++;
                }
            }

            Vector3? bestPos = null;
            HashSet<Transform> bestVisible = null;
            float bestScore = float.MinValue;

            foreach (var candidate in candidatePositions)
            {
                if (refined.Any(st => Vector3.Distance(st.position, candidate) < minSpacing))
                    continue;

                if (IsInsideAnyBuilding(candidate))
                    continue;

                var visible = GetVisibleGradesFromPos(candidate);
                if (visible == null || visible.Count == 0)
                    continue;

                float score = 0f;
                foreach (var grade in visible)
                {
                    int count = observationCounts.TryGetValue(grade, out int c) ? c : 0;
                    if (count < 2)
                        score += (2 - count) * 120f;

                    foreach (var st in refined)
                    {
                        if (st.visibleGrades == null || !st.visibleGrades.Contains(grade))
                            continue;

                        Vector3 dir1 = (st.position - grade.position).normalized;
                        Vector3 dir2 = (candidate - grade.position).normalized;
                        float angle = Vector3.Angle(dir1, dir2);

                        float perpendicularity = 1f - Mathf.Clamp01(Mathf.Abs(angle - 90f) / 90f);
                        score += perpendicularity * 25f;

                        if (angle < 45f || angle > 135f)
                            score -= 40f;
                    }
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    bestPos = candidate;
                    bestVisible = visible;
                }
            }

            if (bestPos == null || bestVisible == null || bestScore <= 0f)
                break;

            refined.Add(new Station
            {
                position = bestPos.Value,
                visibleGrades = bestVisible
            });
        }

        return PruneRedundantStations(refined);
    }

    private List<Station> PruneRedundantStations(List<Station> stations)
    {
        if (stations == null)
            return new List<Station>();

        var pruned = stations
            .Where(s => s != null)
            .Select(s => new Station
            {
                position = s.position,
                visibleGrades = s.visibleGrades != null ? new HashSet<Transform>(s.visibleGrades) : GetVisibleGradesFromPos(s.position)
            })
            .ToList();

        bool removedAny = true;
        while (removedAny && pruned.Count > minStations)
        {
            removedAny = false;

            foreach (var st in pruned)
                st.visibleGrades = GetVisibleGradesFromPos(st.position);

            int baseAcute = CountMarksWithAcuteAngles(pruned);
            var obsCounts = new Dictionary<Transform, int>();
            foreach (var grade in grades) obsCounts[grade] = 0;
            foreach (var st in pruned)
            {
                foreach (var g in st.visibleGrades)
                {
                    if (obsCounts.ContainsKey(g)) obsCounts[g]++;
                }
            }

            int removeIdx = -1;
            int minCritical = int.MaxValue;
            int minCoverage = int.MaxValue;

            for (int i = 0; i < pruned.Count; i++)
            {
                var st = pruned[i];
                int critical = 0;
                foreach (var g in st.visibleGrades)
                {
                    if (obsCounts.TryGetValue(g, out int count) && count <= 2)
                        critical++;
                }

                int coverage = st.visibleGrades.Count;
                if (critical < minCritical || (critical == minCritical && coverage < minCoverage))
                {
                    removeIdx = i;
                    minCritical = critical;
                    minCoverage = coverage;
                }
            }

            if (removeIdx < 0)
                break;

            var candidate = pruned.Where((_, idx) => idx != removeIdx).ToList();
            foreach (var st in candidate)
                st.visibleGrades = GetVisibleGradesFromPos(st.position);

            bool valid = true;
            foreach (var grade in grades)
            {
                int observers = candidate.Count(s => s.visibleGrades.Contains(grade));
                if (observers < 2)
                {
                    valid = false;
                    break;
                }
            }

            if (valid && CountMarksWithAcuteAngles(candidate) <= baseAcute)
            {
                pruned = candidate;
                removedAny = true;
            }
        }

        return pruned;
    }

    // ВСПОМОГАТЕЛЬНЫЙ метод для генерации начальной популяции (обновленный)
    private List<List<Station>> GenerateInitialPopulation(int populationSize, List<Vector3> candidatePositions)
    {
        var population = new List<List<Station>>();

        if (candidatePositions == null || candidatePositions.Count == 0)
        {
            Debug.LogError("Нет кандидатных позиций для генерации начальной популяции!");
            return population;
        }

        if (grades == null || grades.Count == 0)
        {
            Debug.LogError("Нет марок для генерации сети!");
            return population;
        }

        Debug.Log($"Генерация начальной популяции: {populationSize} особей, {candidatePositions.Count} кандидатов, {grades.Count} марок");

        // ================== 1. РАЗНООБРАЗНЫЕ СТРАТЕГИИ ГЕНЕРАЦИИ ==================

        // Определяем, сколько особей создавать каждой стратегией
        int greedyCount = Mathf.Max(2, populationSize / 5);      // 20% - жадные решения
        int randomCount = Mathf.Max(3, populationSize / 4);      // 25% - случайные решения
        int geometricCount = Mathf.Max(2, populationSize / 6);   // ~17% - геометрические решения
        int uniformCount = Mathf.Max(2, populationSize / 6);     // ~17% - равномерное распределение
        int traverseCount = Mathf.Max(3, populationSize / 5);    // 20% - замкнутые ходы
        int restCount = populationSize - greedyCount - randomCount - geometricCount - uniformCount - traverseCount; // Остальные - смешанные

        // ================== 2. ГЕНЕРАЦИЯ ЖАДНЫХ РЕШЕНИЙ (максимальное покрытие) ==================
        for (int i = 0; i < greedyCount && population.Count < populationSize; i++)
        {
            var greedySolution = GenerateGreedySolution(candidatePositions, i);
            if (greedySolution != null && greedySolution.Count >= minStations)
            {
                population.Add(greedySolution);
                Debug.Log($"Жадное решение {i + 1}: {greedySolution.Count} станций");
            }
        }

        // ================== 3. ГЕНЕРАЦИЯ СЛУЧАЙНЫХ РЕШЕНИЙ (разнообразие) ==================
        for (int i = 0; i < randomCount && population.Count < populationSize; i++)
        {
            var randomSolution = GenerateRandomSolution(candidatePositions, i);
            if (randomSolution != null && randomSolution.Count >= minStations)
            {
                population.Add(randomSolution);
                Debug.Log($"Случайное решение {i + 1}: {randomSolution.Count} станций");
            }
        }

        // ================== 4. ГЕНЕРАЦИЯ ГЕОМЕТРИЧЕСКИХ РЕШЕНИЙ (оптимальные углы) ==================
        for (int i = 0; i < geometricCount && population.Count < populationSize; i++)
        {
            var geometricSolution = GenerateGeometricSolution(candidatePositions, i);
            if (geometricSolution != null && geometricSolution.Count >= minStations)
            {
                population.Add(geometricSolution);
                Debug.Log($"Геометрическое решение {i + 1}: {geometricSolution.Count} станций");
            }
        }

        // ================== 5. ГЕНЕРАЦИЯ РАВНОМЕРНЫХ РЕШЕНИЙ (баланс распределения) ==================
        for (int i = 0; i < uniformCount && population.Count < populationSize; i++)
        {
            var uniformSolution = GenerateUniformSolution(candidatePositions, i);
            if (uniformSolution != null && uniformSolution.Count >= minStations)
            {
                population.Add(uniformSolution);
                Debug.Log($"Равномерное решение {i + 1}: {uniformSolution.Count} станций");
            }
        }


        // ================== 5.1. ГЕНЕРАЦИЯ ЗАМКНУТЫХ ПОЛИГОНОМЕТРИЧЕСКИХ ХОДОВ ==================
        for (int i = 0; i < traverseCount && population.Count < populationSize; i++)
        {
            var traverseSolution = GenerateClosedTraverseSolution(candidatePositions, i);
            if (traverseSolution != null && traverseSolution.Count >= minStations)
            {
                population.Add(traverseSolution);
                Debug.Log($"Замкнутый ход {i + 1}: {traverseSolution.Count} станций");
            }
        }

        // ================== 6. ДОПОЛНЕНИЕ СМЕШАННЫМИ РЕШЕНИЯМИ ==================
        while (population.Count < populationSize)
        {
            int strategy = UnityEngine.Random.Range(0, 5);
            List<Station> mixedSolution = null;

            switch (strategy)
            {
                case 0:
                    mixedSolution = GenerateGreedySolution(candidatePositions, population.Count);
                    break;
                case 1:
                    mixedSolution = GenerateRandomSolution(candidatePositions, population.Count);
                    break;
                case 2:
                    mixedSolution = GenerateGeometricSolution(candidatePositions, population.Count);
                    break;
                case 3:
                    mixedSolution = GenerateUniformSolution(candidatePositions, population.Count);
                    break;
                case 4:
                    mixedSolution = GenerateClosedTraverseSolution(candidatePositions, population.Count);
                    break;
            }

            if (mixedSolution != null && mixedSolution.Count >= minStations)
            {
                population.Add(mixedSolution);
            }
            else
            {
                // Последняя попытка: создать минимальное решение
                var minimalSolution = CreateMinimalSolution(candidatePositions);
                if (minimalSolution != null && minimalSolution.Count >= minStations)
                {
                    population.Add(minimalSolution);
                    Debug.Log($"Минимальное решение: {minimalSolution.Count} станций");
                }
            }

            // Защита от бесконечного цикла
            if (population.Count >= populationSize * 2) break;
        }

        // ================== 7. УДАЛЕНИЕ ДУБЛИКАТОВ ==================
        population = RemoveDuplicateSolutions(population);

        // ================== 8. ОБЕСПЕЧЕНИЕ МИНИМАЛЬНОГО РАЗМЕРА ПОПУЛЯЦИИ ==================
        while (population.Count < Mathf.Min(populationSize, 5))
        {
            Debug.LogWarning($"Популяция слишком мала ({population.Count}), добавляем случайные решения");
            var fallback = GenerateGreedySolution(candidatePositions, population.Count);
            if (fallback != null)
                population.Add(fallback);
            else
                break;
        }

        Debug.Log($"✅ Начальная популяция сгенерирована: {population.Count} особей " +
                 $"(жадных: {greedyCount}, случайных: {randomCount}, геометрических: {geometricCount})");

        return population;
    }

    // ================== ВСПОМОГАТЕЛЬНЫЕ МЕТОДЫ ==================


    private List<Station> GenerateClosedTraverseSolution(List<Vector3> candidatePositions, int seed)
    {
        UnityEngine.Random.InitState(seed + System.DateTime.Now.Millisecond * 5);
        if (candidatePositions == null || candidatePositions.Count == 0 || targetBuilding == null)
            return null;

        Bounds bounds = GetObjectBounds(targetBuilding);
        Vector3 center = bounds.center;
        int stationCount = UnityEngine.Random.Range(minStations, Mathf.Min(9, candidatePositions.Count) + 1);
        float sectorStep = 360f / stationCount;
        float startAngle = UnityEngine.Random.Range(0f, sectorStep);

        var solution = new List<Station>();
        for (int i = 0; i < stationCount; i++)
        {
            float sectorCenter = startAngle + i * sectorStep;
            var sectorCandidates = candidatePositions
                .Select(pos => new
                {
                    Position = pos,
                    Visible = GetVisibleGradesFromPos(pos),
                    Delta = Mathf.Abs(Mathf.DeltaAngle(sectorCenter, Mathf.Atan2(pos.z - center.z, pos.x - center.x) * Mathf.Rad2Deg))
                })
                .Where(x => x.Visible.Count > 0 && x.Delta <= sectorStep * 0.65f)
                .OrderByDescending(x => x.Visible.Count * 1000f - x.Delta)
                .ToList();

            foreach (var candidate in sectorCandidates)
            {
                if (solution.Any(s => Vector3.Distance(s.position, candidate.Position) < 8f))
                    continue;

                solution.Add(new Station { position = candidate.Position, visibleGrades = candidate.Visible });
                break;
            }
        }

        if (solution.Count < minStations)
            return null;

        solution = OrderStationsAsClosedTraverse(solution, bounds);

        // Если один из ходовых ходов невидим, пытаемся локально заменить проблемную вершину.
        for (int pass = 0; pass < 2 && !HasClosedTraverse(solution, bounds); pass++)
        {
            for (int i = 0; i < solution.Count; i++)
            {
                int prev = (i - 1 + solution.Count) % solution.Count;
                int next = (i + 1) % solution.Count;
                if (HasLineOfSight(solution[prev].position + Vector3.up * 1.7f, solution[i].position + Vector3.up * 1.7f) &&
                    HasLineOfSight(solution[i].position + Vector3.up * 1.7f, solution[next].position + Vector3.up * 1.7f))
                    continue;

                float currentAngle = Mathf.Atan2(solution[i].position.z - center.z, solution[i].position.x - center.x) * Mathf.Rad2Deg;
                var replacement = candidatePositions
                    .Select(pos => new { Position = pos, Visible = GetVisibleGradesFromPos(pos) })
                    .Where(x => x.Visible.Count > 0 && !solution.Any(s => Vector3.Distance(s.position, x.Position) < 8f))
                    .Where(x => Mathf.Abs(Mathf.DeltaAngle(currentAngle, Mathf.Atan2(x.Position.z - center.z, x.Position.x - center.x) * Mathf.Rad2Deg)) < sectorStep)
                    .Where(x => HasLineOfSight(solution[prev].position + Vector3.up * 1.7f, x.Position + Vector3.up * 1.7f) &&
                                HasLineOfSight(x.Position + Vector3.up * 1.7f, solution[next].position + Vector3.up * 1.7f))
                    .OrderByDescending(x => x.Visible.Count)
                    .FirstOrDefault();

                if (replacement != null)
                    solution[i] = new Station { position = replacement.Position, visibleGrades = replacement.Visible };
            }
        }

        return solution;
    }

    private List<Station> GenerateGreedySolution(List<Vector3> candidatePositions, int seed)
    {
        UnityEngine.Random.InitState(seed + System.DateTime.Now.Millisecond);

        var solution = new List<Station>();
        var uncoveredGrades = new HashSet<Transform>(grades);
        var availableCandidates = new List<Vector3>(candidatePositions);

        // Первая станция: лучший кандидат по покрытию
        Vector3? bestFirstPos = null;
        HashSet<Transform> bestFirstCoverage = null;
        int bestFirstCount = 0;

        foreach (var pos in availableCandidates)
        {
            var coverage = GetVisibleGradesFromPos(pos);
            int newCount = coverage.Count(g => uncoveredGrades.Contains(g));
            if (newCount > bestFirstCount)
            {
                bestFirstCount = newCount;
                bestFirstPos = pos;
                bestFirstCoverage = coverage;
            }
        }

        if (bestFirstPos.HasValue && bestFirstCoverage != null)
        {
            solution.Add(new Station { position = bestFirstPos.Value, visibleGrades = bestFirstCoverage });
            foreach (var g in bestFirstCoverage) uncoveredGrades.Remove(g);
            availableCandidates.Remove(bestFirstPos.Value);
        }

        // Жадное добавление станций, пока не покроем все марки или не достигнем максимума
        while (uncoveredGrades.Count > 0 && solution.Count < 12 && availableCandidates.Count > 0)
        {
            // Перемешиваем кандидатов для разнообразия
            availableCandidates = availableCandidates.OrderBy(x => UnityEngine.Random.value).ToList();

            int bestIdx = -1;
            int bestNew = 0;
            HashSet<Transform> bestCoverage = null;

            // Ищем кандидата, который покрывает больше всего непокрытых марок
            for (int i = 0; i < Mathf.Min(50, availableCandidates.Count); i++) // Ограничиваем проверку
            {
                var pos = availableCandidates[i];
                var coverage = GetVisibleGradesFromPos(pos);
                int newCount = coverage.Count(g => uncoveredGrades.Contains(g));

                if (newCount > bestNew)
                {
                    bestNew = newCount;
                    bestIdx = i;
                    bestCoverage = coverage;
                }
            }

            if (bestIdx >= 0 && bestCoverage != null)
            {
                var bestPos = availableCandidates[bestIdx];
                solution.Add(new Station { position = bestPos, visibleGrades = bestCoverage });

                foreach (var g in bestCoverage) uncoveredGrades.Remove(g);
                availableCandidates.RemoveAt(bestIdx);
            }
            else
            {
                break; // Не нашли полезных кандидатов
            }
        }

        return solution.Count >= minStations ? solution : null;
    }

    private List<Station> GenerateRandomSolution(List<Vector3> candidatePositions, int seed)
    {
        UnityEngine.Random.InitState(seed + System.DateTime.Now.Millisecond * 2);

        // Случайное количество станций (3-8, но не больше доступных кандидатов)
        int stationCount = UnityEngine.Random.Range(
            Mathf.Max(minStations, 3),
            Mathf.Min(8, candidatePositions.Count) + 1
        );

        var shuffled = candidatePositions.OrderBy(x => UnityEngine.Random.value).ToList();
        var solution = new List<Station>();

        for (int i = 0; i < stationCount && i < shuffled.Count; i++)
        {
            var pos = shuffled[i];
            var visible = GetVisibleGradesFromPos(pos);

            if (visible.Count > 0)
            {
                solution.Add(new Station { position = pos, visibleGrades = visible });
            }
            else
            {
                // Если точка не видит марок, попробуем следующую
                continue;
            }
        }

        // Если получилось слишком мало станций, пытаемся добавить ещё
        if (solution.Count < minStations)
        {
            for (int i = stationCount; i < shuffled.Count && solution.Count < minStations; i++)
            {
                var pos = shuffled[i];
                var visible = GetVisibleGradesFromPos(pos);
                if (visible.Count > 0)
                {
                    solution.Add(new Station { position = pos, visibleGrades = visible });
                }
            }
        }

        return solution.Count >= minStations ? solution : null;
    }

    private List<Station> GenerateGeometricSolution(List<Vector3> candidatePositions, int seed)
    {
        UnityEngine.Random.InitState(seed + System.DateTime.Now.Millisecond * 3);

        if (targetBuilding == null) return null;

        Bounds bounds = GetObjectBounds(targetBuilding);
        Vector3 center = bounds.center;
        float optimalDistance = CalculateOptimalDistance(bounds);

        var solution = new List<Station>();

        // Выбираем точки с разных сторон от здания для хорошей геометрии
        int sectors = 4 + (seed % 3); // 4-6 секторов
        float angleStep = 360f / sectors;

        for (int sector = 0; sector < sectors && solution.Count < 8; sector++)
        {
            float baseAngle = sector * angleStep + UnityEngine.Random.Range(-15f, 15f);

            // Ищем кандидатов в этом секторе
            var sectorCandidates = candidatePositions
                .Where(pos =>
                {
                    Vector3 dir = pos - center;
                    float angle = Mathf.Atan2(dir.z, dir.x) * Mathf.Rad2Deg;
                    angle = (angle + 360f) % 360f;

                    float sectorStart = (baseAngle - angleStep * 0.5f + 360f) % 360f;
                    float sectorEnd = (baseAngle + angleStep * 0.5f + 360f) % 360f;

                    if (sectorStart <= sectorEnd)
                        return angle >= sectorStart && angle <= sectorEnd;
                    else
                        return angle >= sectorStart || angle <= sectorEnd;
                })
                .OrderBy(pos => UnityEngine.Random.value)
                .ToList();

            // Берем лучшего кандидата из сектора
            foreach (var pos in sectorCandidates)
            {
                var visible = GetVisibleGradesFromPos(pos);
                if (visible.Count > 0)
                {
                    // Проверяем, что не слишком близко к другим станциям
                    bool tooClose = solution.Any(s => Vector3.Distance(s.position, pos) < 5f);
                    if (!tooClose)
                    {
                        solution.Add(new Station { position = pos, visibleGrades = visible });
                        break;
                    }
                }
            }
        }

        // Если не набрали достаточно станций, добавляем случайные
        if (solution.Count < minStations)
        {
            var randomAddition = GenerateRandomSolution(candidatePositions, seed + 1000);
            if (randomAddition != null)
            {
                foreach (var station in randomAddition)
                {
                    if (!solution.Any(s => Vector3.Distance(s.position, station.position) < 3f))
                    {
                        solution.Add(station);
                    }
                }
            }
        }

        return solution.Count >= minStations ? solution : null;
    }

    private List<Station> GenerateUniformSolution(List<Vector3> candidatePositions, int seed)
    {
        UnityEngine.Random.InitState(seed + System.DateTime.Now.Millisecond * 4);

        if (targetBuilding == null) return null;

        Bounds bounds = GetObjectBounds(targetBuilding);
        Vector3 center = bounds.center;

        var solution = new List<Station>();

        // Разбиваем пространство на квадранты и берем по точке из каждого
        int quadrants = 4;
        for (int q = 0; q < quadrants && solution.Count < 6; q++)
        {
            // Определяем квадрант
            float minX = center.x + ((q % 2 == 0) ? 0 : 10f);
            float maxX = center.x + ((q % 2 == 0) ? -10f : 0);
            float minZ = center.z + ((q < 2) ? 0 : 10f);
            float maxZ = center.z + ((q < 2) ? -10f : 0);

            var quadrantCandidates = candidatePositions
                .Where(pos => pos.x >= Mathf.Min(minX, maxX) && pos.x <= Mathf.Max(minX, maxX) &&
                             pos.z >= Mathf.Min(minZ, maxZ) && pos.z <= Mathf.Max(minZ, maxZ))
                .OrderBy(pos => UnityEngine.Random.value)
                .ToList();

            foreach (var pos in quadrantCandidates)
            {
                var visible = GetVisibleGradesFromPos(pos);
                if (visible.Count > 0)
                {
                    solution.Add(new Station { position = pos, visibleGrades = visible });
                    break;
                }
            }
        }

        return solution.Count >= minStations ? solution : null;
    }

    private List<Station> CreateMinimalSolution(List<Vector3> candidatePositions)
    {
        // Создаем минимальное решение: 3 станции, максимально покрывающие марки
        var solution = new List<Station>();
        var shuffled = candidatePositions.OrderBy(x => UnityEngine.Random.value).ToList();

        for (int i = 0; i < Mathf.Min(3, shuffled.Count) && solution.Count < minStations; i++)
        {
            var pos = shuffled[i];
            var visible = GetVisibleGradesFromPos(pos);
            if (visible.Count > 0)
            {
                solution.Add(new Station { position = pos, visibleGrades = visible });
            }
        }

        return solution.Count >= minStations ? solution : null;
    }

    private List<List<Station>> RemoveDuplicateSolutions(List<List<Station>> population)
    {
        var uniqueSolutions = new List<List<Station>>();
        var solutionHashes = new HashSet<string>();

        foreach (var solution in population)
        {
            // Создаем хэш решения на основе позиций станций
            var positions = solution
                .Select(s => $"{Mathf.RoundToInt(s.position.x * 6)},{Mathf.RoundToInt(s.position.z * 6)}")
                .OrderBy(s => s)
                .Aggregate("", (a, b) => a + "|" + b);

            if (!solutionHashes.Contains(positions))
            {
                solutionHashes.Add(positions);
                uniqueSolutions.Add(solution);
            }
        }

        if (uniqueSolutions.Count < population.Count)
        {
            Debug.Log($"Удалено {population.Count - uniqueSolutions.Count} дубликатов из популяции");
        }

        return uniqueSolutions;
    }

    // Генерация кандидатных позиций вокруг здания
    // Генерация кандидатных позиций вокруг здания (ИСПРАВЛЕННАЯ ВЕРСИЯ)
    private List<Vector3> GenerateCandidatePositions(Bounds bounds, Collider buildingCollider, float offset)
    {
        var candidates = new List<Vector3>();
        float rayStartOffset = 40f; // Увеличено для надёжности

        Debug.Log($"[DEBUG] Генерация кандидатов: offset={offset:F1}, bounds={bounds.size:F1}, grades={grades?.Count ?? 0}");

        // ===== 1. ТОЧКИ ПО ПЕРИМЕТРУ ЗДАНИЯ (всегда генерируем) =====
        int pointsPerSide = Mathf.Max(12, 360 / 15); // Минимум 12 точек

        for (int i = 0; i < 360; i += 360 / pointsPerSide)
        {
            float angle = i * Mathf.Deg2Rad;

            // Несколько колец на разных расстояниях
            for (int ring = 0; ring < 3; ring++)
            {
                float distance = offset * (0.5f + ring * 0.25f);
                Vector3 dir = new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle));
                Vector3 point = bounds.center + dir * distance;

                Vector3 rayStart = point + Vector3.up * rayStartOffset;

                if (Physics.Raycast(rayStart, Vector3.down, out RaycastHit hit,
                    rayStartOffset + 50f, groundLayer))
                {
                    Vector3 groundPos = hit.point;

                    // Только проверка на нахождение в здании
                    if (!IsInsideAnyBuilding(groundPos))
                    {
                        // Минимальное расстояние до стены (уменьшено)
                        Vector3 wallPoint = buildingCollider != null ? buildingCollider.ClosestPoint(groundPos) : groundPos;
                        if (Vector3.Distance(groundPos, wallPoint) > 1.5f)
                        {
                            candidates.Add(groundPos);
                        }
                    }
                }
            }
        }

        // ===== 2. ТОЧКИ ВОКРУГ КАЖДОЙ МАРКИ (ВСЕГДА, независимо от количества!) =====
        // ✅ УБРАНО ограничение grades.Count <= 20
        if (grades != null)
        {
            foreach (var grade in grades)
            {
                if (grade == null) continue;

                Vector3 toGrade = (grade.position - bounds.center).normalized;

                // Больше углов для лучшего покрытия
                float[] angleOffsets = { -45f, -30f, -15f, 0f, 15f, 30f, 45f };

                foreach (float angleOffset in angleOffsets)
                {
                    Vector3 dir = Quaternion.Euler(0, angleOffset, 0) * toGrade;

                    // Больше расстояний
                    for (int d = 0; d < 3; d++)
                    {
                        float distance = offset * (0.4f + d * 0.3f);
                        Vector3 point = grade.position + dir * distance;

                        Vector3 rayStart = point + Vector3.up * rayStartOffset;

                        if (Physics.Raycast(rayStart, Vector3.down, out RaycastHit hit,
                            rayStartOffset + 50f, groundLayer))
                        {
                            Vector3 groundPos = hit.point;

                            if (!IsInsideAnyBuilding(groundPos))
                            {
                                candidates.Add(groundPos);
                            }
                        }
                    }
                }
            }
        }

        // ===== 3. СЛУЧАЙНЫЕ ТОЧКИ (если кандидатов мало) =====
        if (candidates.Count < 30)
        {
            Debug.Log($"[DEBUG] Мало кандидатов ({candidates.Count}), добавляем случайные точки...");

            int attempts = 0;
            while (candidates.Count < 50 && attempts < 500)
            {
                attempts++;

                // Случайная точка в расширенном боксе вокруг здания
                Vector3 randomPoint = bounds.center + new Vector3(
                    UnityEngine.Random.Range(-1f, 1f) * (bounds.size.x + offset),
                    0,
                    UnityEngine.Random.Range(-1f, 1f) * (bounds.size.z + offset)
                );

                Vector3 rayStart = randomPoint + Vector3.up * rayStartOffset;

                if (Physics.Raycast(rayStart, Vector3.down, out RaycastHit hit,
                    rayStartOffset + 50f, groundLayer))
                {
                    Vector3 groundPos = hit.point;

                    if (!IsInsideAnyBuilding(groundPos))
                    {
                        candidates.Add(groundPos);
                    }
                }
            }
        }

        // ===== 4. УДАЛЕНИЕ ДУБЛИКАТОВ (с меньшим порогом) =====
        var uniqueCandidates = new List<Vector3>();
        foreach (var candidate in candidates)
        {
            bool isDuplicate = false;
            foreach (var unique in uniqueCandidates)
            {
                if (Vector3.Distance(candidate, unique) < 3f) // Уменьшен порог
                {
                    isDuplicate = true;
                    break;
                }
            }
            if (!isDuplicate)
                uniqueCandidates.Add(candidate);
        }

        Debug.Log($"[DEBUG] ✅ Сгенерировано {uniqueCandidates.Count} валидных позиций " +
                 $"(было {candidates.Count}, grades={grades?.Count ?? 0})");

        // ===== 5. КРИТИЧЕСКАЯ ПРОВЕРКА =====
        if (uniqueCandidates.Count == 0)
        {
            Debug.LogError("[CRITICAL] Не сгенерировано ни одной кандидатной позиции!");
            Debug.LogError($"[CRITICAL] Проверьте: 1) groundLayer={groundLayer.value}, " +
                          $"2) buildingCollider={buildingCollider != null}, " +
                          $"3) bounds={bounds}");

            // Создаём точки принудительно вокруг центра здания
            for (int i = 0; i < 8; i++)
            {
                float angle = i * 45f * Mathf.Deg2Rad;
                Vector3 dir = new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle));
                Vector3 point = bounds.center + dir * offset;
                uniqueCandidates.Add(point);
            }

            Debug.LogWarning($"[CRITICAL] Создано {uniqueCandidates.Count} точек принудительно");
        }

        return uniqueCandidates;
    }

    private float CalculateOptimalDistance(Bounds bounds)
    {
        float buildingSize = bounds.size.magnitude;

        // Увеличиваем расстояния для лучшего покрытия
        float minDistance = Mathf.Max(buildingSize * 0.15f, 8f);
        float maxDistance = Mathf.Max(buildingSize * 0.4f, 15f);

        Debug.Log($"[DEBUG] Размер здания: {buildingSize:F1}m, " +
                 $"оптимальное расстояние: {minDistance:F1}-{maxDistance:F1}m");

        return (minDistance + maxDistance) * 0.5f;
    }


    private List<Station> OrderStationsAsClosedTraverse(List<Station> stations, Bounds bounds)
    {
        if (stations == null)
            return new List<Station>();

        Vector3 center = bounds.center;
        return stations
            .Where(s => s != null)
            .OrderBy(s => Mathf.Atan2(s.position.z - center.z, s.position.x - center.x))
            .Select(s => new Station
            {
                position = s.position,
                networkObj = s.networkObj,
                ms60 = s.ms60,
                visibleGrades = s.visibleGrades != null
                    ? new HashSet<Transform>(s.visibleGrades)
                    : GetVisibleGradesFromPos(s.position)
            })
            .ToList();
    }

    private int CountClosedTraverseLinks(List<Station> stations, Bounds bounds)
    {
        if (stations == null || stations.Count < 3)
            return 0;

        var ordered = OrderStationsAsClosedTraverse(stations, bounds);
        int links = 0;
        for (int i = 0; i < ordered.Count; i++)
        {
            Vector3 from = ordered[i].position + Vector3.up * 1.7f;
            Vector3 to = ordered[(i + 1) % ordered.Count].position + Vector3.up * 1.7f;
            if (HasLineOfSight(from, to))
                links++;
        }

        return links;
    }

    private bool HasClosedTraverse(List<Station> stations, Bounds bounds)
    {
        return stations != null && stations.Count >= minStations && CountClosedTraverseLinks(stations, bounds) == stations.Count;
    }

    private List<Station> EnsureClosedTraverseConnectivity(List<Station> stations, List<Vector3> candidatePositions, Bounds bounds)
    {
        var traverse = OrderStationsAsClosedTraverse(stations, bounds);
        if (traverse.Count < minStations || candidatePositions == null || candidatePositions.Count == 0)
            return traverse;

        const int maxRepairPasses = 4;
        for (int pass = 0; pass < maxRepairPasses; pass++)
        {
            bool insertedAny = false;
            traverse = OrderStationsAsClosedTraverse(traverse, bounds);

            for (int i = 0; i < traverse.Count; i++)
            {
                int nextIndex = (i + 1) % traverse.Count;
                Vector3 from = traverse[i].position + Vector3.up * 1.7f;
                Vector3 to = traverse[nextIndex].position + Vector3.up * 1.7f;
                if (HasLineOfSight(from, to))
                    continue;

                var bridge = FindTraverseBridgeStations(
                    traverse[i].position,
                    traverse[nextIndex].position,
                    traverse,
                    candidatePositions,
                    bounds);

                if (bridge.Count == 0)
                {
                    Debug.LogWarning($"Не удалось восстановить прямую видимость хода между станциями {i + 1} и {nextIndex + 1}");
                    continue;
                }

                traverse.InsertRange(i + 1, bridge);
                insertedAny = true;
                Debug.Log($"Добавлено {bridge.Count} промежуточных станций для замыкания хода между {i + 1} и {nextIndex + 1}");
                i += bridge.Count;
            }

            if (!insertedAny || HasClosedTraverse(traverse, bounds))
                break;
        }

        return OrderStationsAsClosedTraverse(traverse, bounds);
    }

    private List<Station> FindTraverseBridgeStations(
        Vector3 start,
        Vector3 end,
        List<Station> currentTraverse,
        List<Vector3> candidatePositions,
        Bounds bounds)
    {
        const int maxBridgeCandidates = 90;
        const float minSpacing = 5f;

        var center = bounds.center;
        var startFlat = new Vector2(start.x, start.z);
        var endFlat = new Vector2(end.x, end.z);

        var repairPool = new List<Vector3>(candidatePositions);
        repairPool.AddRange(GenerateBridgeCandidatePositions(start, end, bounds));

        var bridgeCandidates = repairPool
            .Where(pos => !IsInsideAnyBuilding(pos))
            .Where(pos => currentTraverse.All(st => Vector3.Distance(st.position, pos) >= minSpacing))
            .Select(pos => new
            {
                Position = pos,
                Visible = GetVisibleGradesFromPos(pos),
                SegmentDistance = DistancePointToSegment(new Vector2(pos.x, pos.z), startFlat, endFlat),
                CenterDistance = Mathf.Abs(
                    Vector3.Distance(new Vector3(pos.x, 0f, pos.z), new Vector3(center.x, 0f, center.z)) -
                    (Vector3.Distance(new Vector3(start.x, 0f, start.z), new Vector3(center.x, 0f, center.z)) +
                     Vector3.Distance(new Vector3(end.x, 0f, end.z), new Vector3(center.x, 0f, center.z))) * 0.5f)
            })
            .Where(x => x.Visible.Count > 0)
            .OrderBy(x => x.SegmentDistance + x.CenterDistance * 0.25f)
            .Take(maxBridgeCandidates)
            .ToList();

        var nodes = new List<Vector3> { start };
        nodes.AddRange(bridgeCandidates.Select(x => x.Position));
        nodes.Add(end);

        int endNode = nodes.Count - 1;
        var previous = Enumerable.Repeat(-1, nodes.Count).ToArray();
        var queue = new Queue<int>();
        queue.Enqueue(0);
        previous[0] = 0;

        while (queue.Count > 0 && previous[endNode] < 0)
        {
            int node = queue.Dequeue();
            for (int other = 1; other < nodes.Count; other++)
            {
                if (previous[other] >= 0)
                    continue;

                if (!HasLineOfSight(nodes[node] + Vector3.up * 1.7f, nodes[other] + Vector3.up * 1.7f))
                    continue;

                previous[other] = node;
                queue.Enqueue(other);
                if (other == endNode)
                    break;
            }
        }

        if (previous[endNode] < 0)
            return new List<Station>();

        var path = new List<int>();
        for (int at = endNode; at != 0; at = previous[at])
            path.Add(at);
        path.Reverse();

        return path
            .Where(idx => idx != endNode)
            .Select(idx => new Station
            {
                position = nodes[idx],
                visibleGrades = GetVisibleGradesFromPos(nodes[idx])
            })
            .ToList();
    }

    private List<Vector3> GenerateBridgeCandidatePositions(Vector3 start, Vector3 end, Bounds bounds)
    {
        var generated = new List<Vector3>();
        Vector3 segment = end - start;
        Vector3 horizontal = new Vector3(segment.x, 0f, segment.z);
        if (horizontal.sqrMagnitude < 0.001f)
            return generated;

        Vector3 direction = horizontal.normalized;
        Vector3 perpendicular = new Vector3(-direction.z, 0f, direction.x);
        float length = horizontal.magnitude;
        float step = Mathf.Clamp(length / 4f, 8f, 20f);
        int steps = Mathf.Clamp(Mathf.CeilToInt(length / step), 2, 8);
        float[] sideOffsets = { 0f, 6f, -6f, 12f, -12f };
        float rayStartOffset = Mathf.Max(bounds.size.y + 20f, 40f);

        for (int i = 1; i < steps; i++)
        {
            float t = i / (float)steps;
            Vector3 basePoint = Vector3.Lerp(start, end, t);
            foreach (float sideOffset in sideOffsets)
            {
                Vector3 probe = basePoint + perpendicular * sideOffset;
                Vector3 rayStart = probe + Vector3.up * rayStartOffset;
                if (Physics.Raycast(rayStart, Vector3.down, out RaycastHit hit, rayStartOffset + 80f, groundLayer))
                {
                    Vector3 grounded = hit.point;
                    if (!IsInsideAnyBuilding(grounded) && !generated.Any(p => Vector3.Distance(p, grounded) < 3f))
                        generated.Add(grounded);
                }
            }
        }

        return generated;
    }

    private float DistancePointToSegment(Vector2 point, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float denominator = Vector2.Dot(ab, ab);
        if (denominator <= 0.0001f)
            return Vector2.Distance(point, a);

        float t = Mathf.Clamp01(Vector2.Dot(point - a, ab) / denominator);
        return Vector2.Distance(point, a + ab * t);
    }

    private float CalculateClosedTraverseShapeScore(List<Station> stations, Bounds bounds)
    {
        if (stations == null || stations.Count < 3)
            return 0f;

        var ordered = OrderStationsAsClosedTraverse(stations, bounds);
        Vector3 center = bounds.center;
        float targetRadius = 0f;
        foreach (var station in ordered)
            targetRadius += Vector3.Distance(new Vector3(station.position.x, 0f, station.position.z), new Vector3(center.x, 0f, center.z));
        targetRadius /= ordered.Count;

        float radiusPenalty = 0f;
        float minSide = float.MaxValue;
        float maxSide = 0f;
        float turnScore = 0f;

        for (int i = 0; i < ordered.Count; i++)
        {
            Vector3 prev = ordered[(i - 1 + ordered.Count) % ordered.Count].position;
            Vector3 cur = ordered[i].position;
            Vector3 next = ordered[(i + 1) % ordered.Count].position;

            float radius = Vector3.Distance(new Vector3(cur.x, 0f, cur.z), new Vector3(center.x, 0f, center.z));
            if (targetRadius > 0.001f)
                radiusPenalty += Mathf.Abs(radius - targetRadius) / targetRadius;

            float side = Vector3.Distance(cur, next);
            minSide = Mathf.Min(minSide, side);
            maxSide = Mathf.Max(maxSide, side);

            Vector3 a = (prev - cur).normalized;
            Vector3 b = (next - cur).normalized;
            float angle = Vector3.Angle(a, b);
            if (angle >= 60f && angle <= 160f)
                turnScore += 1f;
        }

        float radiusScore = Mathf.Clamp01(1f - radiusPenalty / ordered.Count);
        float sideScore = maxSide > 0.001f ? Mathf.Clamp01(minSide / maxSide) : 0f;
        float angleScore = turnScore / ordered.Count;

        return (radiusScore * 0.4f + sideScore * 0.3f + angleScore * 0.3f) * 100000f;
    }

    private int CountMarksWithAtLeastTwoObservers(List<Station> solution)
    {
        if (solution == null || grades == null || grades.Count == 0)
            return 0;

        var counts = new Dictionary<Transform, int>();
        foreach (var grade in grades)
            counts[grade] = 0;

        foreach (var station in solution)
        {
            if (station?.visibleGrades == null) continue;
            foreach (var grade in station.visibleGrades)
            {
                if (counts.ContainsKey(grade))
                    counts[grade]++;
            }
        }

        return counts.Values.Count(v => v >= 2);
    }

    private int CountMarksWithAcuteAngles(List<Station> solution, float acuteThresholdDeg = 45f)
    {
        if (solution == null || grades == null || grades.Count == 0)
            return 0;

        int acuteMarks = 0;
        foreach (var grade in grades)
        {
            var observingStations = solution
                .Where(st => st?.visibleGrades != null && st.visibleGrades.Contains(grade))
                .Select(st => st.position)
                .ToList();

            if (observingStations.Count < 2)
                continue;

            float minAngle = 180f;
            for (int i = 0; i < observingStations.Count - 1; i++)
            {
                for (int j = i + 1; j < observingStations.Count; j++)
                {
                    Vector3 dir1 = (observingStations[i] - grade.position).normalized;
                    Vector3 dir2 = (observingStations[j] - grade.position).normalized;
                    float angle = Vector3.Angle(dir1, dir2);
                    minAngle = Mathf.Min(minAngle, angle);
                }
            }

            if (minAngle < acuteThresholdDeg)
                acuteMarks++;
        }

        return acuteMarks;
    }

    private List<Vector3> SelectBalancedCandidatesByGradeSides(List<Vector3> candidates, Bounds bounds, int maxCount)
    {
        if (candidates == null || candidates.Count <= maxCount || !GradesExistOnBothSides(bounds))
            return candidates;

        Vector3 axis = GetDominantGradeAxis(bounds);
        var negative = candidates
            .Where(pos => GetSignedSide(pos, bounds.center, axis) < 0f)
            .OrderBy(_ => UnityEngine.Random.value)
            .ToList();
        var positive = candidates
            .Where(pos => GetSignedSide(pos, bounds.center, axis) >= 0f)
            .OrderBy(_ => UnityEngine.Random.value)
            .ToList();

        int half = maxCount / 2;
        var selected = new List<Vector3>();
        selected.AddRange(negative.Take(half));
        selected.AddRange(positive.Take(half));

        if (selected.Count < maxCount)
        {
            selected.AddRange(candidates
                .Where(pos => !selected.Any(existing => Vector3.Distance(existing, pos) < 0.01f))
                .OrderBy(_ => UnityEngine.Random.value)
                .Take(maxCount - selected.Count));
        }

        return selected;
    }

    private List<Station> FinalizeNetworkQuality(List<Station> stations, List<Vector3> candidatePositions, Bounds bounds)
    {
        var finalized = EnsureClosedTraverseConnectivity(stations, candidatePositions, bounds);
        finalized = EnsureStationsOnBothGradeSides(finalized, candidatePositions, bounds);
        finalized = EnsureFullGradeCoverage(finalized, candidatePositions, bounds, requiredObservers: 2);
        finalized = EnsureClosedTraverseConnectivity(finalized, candidatePositions, bounds);
        finalized = EnsureExcellentConditioning(finalized, candidatePositions, bounds);
        finalized = EnsureFullGradeCoverage(finalized, candidatePositions, bounds, requiredObservers: 2);
        finalized = EnsureClosedTraverseConnectivity(finalized, candidatePositions, bounds);
        return PruneStationsForTwoObserverClosedNetwork(finalized, bounds);
    }

    private List<Station> EnsureFullGradeCoverage(List<Station> stations, List<Vector3> candidatePositions, Bounds bounds, int requiredObservers)
    {
        var result = stations == null
            ? new List<Station>()
            : stations.Where(s => s != null).Select(s => new Station
            {
                position = s.position,
                visibleGrades = s.visibleGrades != null ? new HashSet<Transform>(s.visibleGrades) : GetVisibleGradesFromPos(s.position)
            }).ToList();

        if (grades == null || grades.Count == 0)
            return OrderStationsAsClosedTraverse(result, bounds);

        var repairPool = new List<Vector3>();
        if (candidatePositions != null)
            repairPool.AddRange(candidatePositions);
        repairPool.AddRange(GenerateGradeFocusedRepairCandidates(bounds));
        repairPool = RemoveDuplicateCandidatePositions(repairPool, 3f);

        int maxRepairStations = Mathf.Clamp(Mathf.CeilToInt(grades.Count / 2f), 2, 5);
        const float minSpacing = 5f;
        for (int added = 0; added < maxRepairStations; added++)
        {
            foreach (var station in result)
                station.visibleGrades = GetVisibleGradesFromPos(station.position);

            var observationCounts = grades.ToDictionary(g => g, _ => 0);
            foreach (var station in result)
            {
                if (station.visibleGrades == null) continue;
                foreach (var grade in station.visibleGrades)
                {
                    if (observationCounts.ContainsKey(grade))
                        observationCounts[grade]++;
                }
            }

            var weakGrades = observationCounts
                .Where(pair => pair.Value < requiredObservers)
                .Select(pair => pair.Key)
                .ToList();

            if (weakGrades.Count == 0)
                break;

            Vector3? bestPos = null;
            HashSet<Transform> bestVisible = null;
            float bestScore = float.MinValue;

            foreach (var candidate in repairPool)
            {
                if (IsInsideAnyBuilding(candidate) || result.Any(st => Vector3.Distance(st.position, candidate) < minSpacing))
                    continue;

                var visible = GetVisibleGradesFromPos(candidate);
                if (visible == null || visible.Count == 0)
                    continue;

                int repairedWeakGrades = visible.Count(g => weakGrades.Contains(g));
                if (repairedWeakGrades == 0)
                    continue;

                float sideBonus = 0f;
                if (GradesExistOnBothSides(bounds))
                {
                    Vector3 axis = GetDominantGradeAxis(bounds);
                    sideBonus = visible.Count(g => Mathf.Sign(GetSignedSide(g.position, bounds.center, axis)) ==
                                                  Mathf.Sign(GetSignedSide(candidate, bounds.center, axis))) * 75f;
                }

                float score = repairedWeakGrades * 10000f + visible.Count * 250f + sideBonus;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestPos = candidate;
                    bestVisible = visible;
                }
            }

            if (!bestPos.HasValue || bestVisible == null)
            {
                Debug.LogWarning($"Не удалось добавить станцию для полного покрытия: осталось слабых марок {weakGrades.Count}");
                break;
            }

            result.Add(new Station { position = bestPos.Value, visibleGrades = bestVisible });
            Debug.Log($"Добавлена ремонтная станция: закрыто слабых марок {bestVisible.Count(g => weakGrades.Contains(g))}/{weakGrades.Count}");
        }

        return OrderStationsAsClosedTraverse(result, bounds);
    }

    private List<Station> PruneStationsForTwoObserverClosedNetwork(List<Station> stations, Bounds bounds)
    {
        var result = OrderStationsAsClosedTraverse(stations, bounds);
        if (result.Count <= minStations || grades == null || grades.Count == 0)
            return result;

        foreach (var station in result)
            station.visibleGrades = GetVisibleGradesFromPos(station.position);

        int targetStationCount = Mathf.Max(minStations, Mathf.CeilToInt(grades.Count / 2f));
        bool removedAny = true;

        while (removedAny && result.Count > targetStationCount)
        {
            removedAny = false;
            int removeIndex = FindBestRemovableStationIndex(result, bounds);
            if (removeIndex < 0)
                break;

            result.RemoveAt(removeIndex);
            result = OrderStationsAsClosedTraverse(result, bounds);
            removedAny = true;
        }

        return result;
    }

    private int FindBestRemovableStationIndex(List<Station> stations, Bounds bounds)
    {
        int bestIndex = -1;
        float bestScore = float.MinValue;

        for (int i = 0; i < stations.Count; i++)
        {
            var candidate = stations.Where((_, index) => index != i).ToList();
            foreach (var station in candidate)
                station.visibleGrades = GetVisibleGradesFromPos(station.position);

            if (candidate.Count < minStations || !HasRequiredGradeObservers(candidate, requiredObservers: 2))
                continue;

            // Удаляем только те станции, после которых оставшиеся станции поочередно
            // видят друг друга в замкнутом ходе T1-T2-...-Tn-T1.
            if (!HasClosedTraverse(candidate, bounds))
                continue;

            int redundantObservations = CountObservationsAboveRequired(candidate, requiredObservers: 2);
            float stationCoverage = stations[i].visibleGrades != null ? stations[i].visibleGrades.Count : 0f;
            float score = redundantObservations * 1000f - stationCoverage;

            if (score > bestScore)
            {
                bestScore = score;
                bestIndex = i;
            }
        }

        return bestIndex;
    }

    private bool HasRequiredGradeObservers(List<Station> stations, int requiredObservers)
    {
        return CountGradeObservers(stations).Values.All(count => count >= requiredObservers);
    }

    private int CountObservationsAboveRequired(List<Station> stations, int requiredObservers)
    {
        return CountGradeObservers(stations).Values.Sum(count => Mathf.Max(0, count - requiredObservers));
    }

    private Dictionary<Transform, int> CountGradeObservers(List<Station> stations)
    {
        var counts = new Dictionary<Transform, int>();
        if (grades == null)
            return counts;

        foreach (var grade in grades)
            counts[grade] = 0;

        if (stations == null)
            return counts;

        foreach (var station in stations)
        {
            if (station?.visibleGrades == null) continue;
            foreach (var grade in station.visibleGrades)
            {
                if (counts.ContainsKey(grade))
                    counts[grade]++;
            }
        }

        return counts;
    }

    private List<Vector3> GenerateGradeFocusedRepairCandidates(Bounds bounds)
    {
        var generated = new List<Vector3>();
        if (grades == null)
            return generated;

        float rayStartOffset = Mathf.Max(bounds.size.y + 20f, 40f);
        float baseDistance = CalculateOptimalDistance(bounds);
        float[] angleOffsets = { -60f, -35f, -15f, 0f, 15f, 35f, 60f, 180f };
        float[] distanceFactors = { 0.35f, 0.55f, 0.8f, 1.1f };

        foreach (var grade in grades)
        {
            if (grade == null) continue;

            Vector3 outward = grade.position - bounds.center;
            outward.y = 0f;
            if (outward.sqrMagnitude < 0.001f)
                outward = bounds.size.x >= bounds.size.z ? Vector3.forward : Vector3.right;
            outward.Normalize();

            foreach (float angleOffset in angleOffsets)
            {
                Vector3 dir = Quaternion.Euler(0f, angleOffset, 0f) * outward;
                foreach (float factor in distanceFactors)
                {
                    Vector3 probe = grade.position + dir * baseDistance * factor;
                    Vector3 rayStart = probe + Vector3.up * rayStartOffset;
                    if (Physics.Raycast(rayStart, Vector3.down, out RaycastHit hit, rayStartOffset + 80f, groundLayer))
                    {
                        Vector3 grounded = hit.point;
                        if (!IsInsideAnyBuilding(grounded))
                            generated.Add(grounded);
                    }
                }
            }
        }

        return generated;
    }

    private List<Vector3> RemoveDuplicateCandidatePositions(List<Vector3> candidates, float minDistance)
    {
        var unique = new List<Vector3>();
        if (candidates == null)
            return unique;

        foreach (var candidate in candidates)
        {
            if (!unique.Any(existing => Vector3.Distance(existing, candidate) < minDistance))
                unique.Add(candidate);
        }

        return unique;
    }

    private List<Station> EnsureStationsOnBothGradeSides(List<Station> stations, List<Vector3> candidatePositions, Bounds bounds)
    {
        var result = OrderStationsAsClosedTraverse(stations, bounds);
        if (candidatePositions == null || candidatePositions.Count == 0 || !GradesExistOnBothSides(bounds))
            return result;

        Vector3 axis = GetDominantGradeAxis(bounds);
        bool hasNegativeStation = result.Any(st => GetSignedSide(st.position, bounds.center, axis) < 0f);
        bool hasPositiveStation = result.Any(st => GetSignedSide(st.position, bounds.center, axis) >= 0f);

        if (hasNegativeStation && hasPositiveStation)
            return result;

        bool needPositive = !hasPositiveStation;
        Vector3? bestPos = null;
        HashSet<Transform> bestVisible = null;
        float bestScore = float.MinValue;

        foreach (var candidate in candidatePositions)
        {
            float side = GetSignedSide(candidate, bounds.center, axis);
            if ((needPositive && side < 0f) || (!needPositive && side >= 0f))
                continue;

            if (IsInsideAnyBuilding(candidate) || result.Any(st => Vector3.Distance(st.position, candidate) < 6f))
                continue;

            var visible = GetVisibleGradesFromPos(candidate);
            if (visible == null || visible.Count == 0)
                continue;

            int sameSideGrades = visible.Count(g => IsGradeOnRequestedSide(g, bounds, axis, needPositive));
            if (sameSideGrades == 0)
                continue;

            float wallSeparation = Mathf.Abs(side);
            float score = sameSideGrades * 1000f + visible.Count * 100f + wallSeparation;
            if (score > bestScore)
            {
                bestScore = score;
                bestPos = candidate;
                bestVisible = visible;
            }
        }

        if (bestPos.HasValue && bestVisible != null)
        {
            result.Add(new Station { position = bestPos.Value, visibleGrades = bestVisible });
            Debug.Log($"Добавлена станция на {(needPositive ? "положительной" : "отрицательной")} стороне здания для двухстороннего покрытия марок");
        }
        else
        {
            Debug.LogWarning($"Не удалось подобрать станцию на {(needPositive ? "положительной" : "отрицательной")} стороне здания");
        }

        return OrderStationsAsClosedTraverse(result, bounds);
    }

    private bool GradesExistOnBothSides(Bounds bounds)
    {
        if (grades == null || grades.Count < 2)
            return false;

        Vector3 axis = GetDominantGradeAxis(bounds);
        bool hasNegative = false;
        bool hasPositive = false;
        foreach (var grade in grades)
        {
            if (grade == null) continue;
            if (GetSignedSide(grade.position, bounds.center, axis) < 0f) hasNegative = true;
            else hasPositive = true;
        }

        return hasNegative && hasPositive;
    }

    private Vector3 GetDominantGradeAxis(Bounds bounds)
    {
        if (grades == null || grades.Count == 0)
            return bounds.size.x >= bounds.size.z ? Vector3.right : Vector3.forward;

        float minX = float.MaxValue, maxX = float.MinValue;
        float minZ = float.MaxValue, maxZ = float.MinValue;
        foreach (var grade in grades)
        {
            if (grade == null) continue;
            minX = Mathf.Min(minX, grade.position.x);
            maxX = Mathf.Max(maxX, grade.position.x);
            minZ = Mathf.Min(minZ, grade.position.z);
            maxZ = Mathf.Max(maxZ, grade.position.z);
        }

        return (maxX - minX) >= (maxZ - minZ) ? Vector3.right : Vector3.forward;
    }

    private float GetSignedSide(Vector3 position, Vector3 center, Vector3 axis)
    {
        return Vector3.Dot(position - center, axis.normalized);
    }

    private bool IsGradeOnRequestedSide(Transform grade, Bounds bounds, Vector3 axis, bool positiveSide)
    {
        if (grade == null)
            return false;

        float side = GetSignedSide(grade.position, bounds.center, axis);
        return positiveSide ? side >= 0f : side < 0f;
    }

    private float CalculateTwoSidedStationScore(List<Station> solution, Bounds bounds)
    {
        if (solution == null || solution.Count == 0 || !GradesExistOnBothSides(bounds))
            return 0f;

        Vector3 axis = GetDominantGradeAxis(bounds);
        int negativeStations = solution.Count(st => st != null && GetSignedSide(st.position, bounds.center, axis) < 0f);
        int positiveStations = solution.Count(st => st != null && GetSignedSide(st.position, bounds.center, axis) >= 0f);

        if (negativeStations == 0 || positiveStations == 0)
            return -250000f;

        int minSide = Mathf.Min(negativeStations, positiveStations);
        int maxSide = Mathf.Max(negativeStations, positiveStations);
        float balance = maxSide > 0 ? minSide / (float)maxSide : 0f;
        return 120000f + balance * 80000f;
    }

    private const double ExcellentConditionThreshold = 1000.0;

    private List<Station> EnsureExcellentConditioning(List<Station> stations, List<Vector3> candidatePositions, Bounds bounds)
    {
        var improved = OrderStationsAsClosedTraverse(stations, bounds);
        if (candidatePositions == null || candidatePositions.Count == 0)
            return improved;

        double currentCond = CalculatePlanConditionNumberForSolution(improved);
        if (currentCond <= ExcellentConditionThreshold)
        {
            Debug.Log($"Число обусловленности сети отличное: {currentCond:N0}");
            return improved;
        }

        const int maxConditionStations = 4;
        const float minSpacing = 6f;
        for (int step = 0; step < maxConditionStations && currentCond > ExcellentConditionThreshold; step++)
        {
            Vector3? bestPos = null;
            HashSet<Transform> bestVisible = null;
            double bestCond = currentCond;

            foreach (var candidate in candidatePositions)
            {
                if (IsInsideAnyBuilding(candidate))
                    continue;

                if (improved.Any(st => Vector3.Distance(st.position, candidate) < minSpacing))
                    continue;

                var visible = GetVisibleGradesFromPos(candidate);
                if (visible == null || visible.Count < 2)
                    continue;

                var trial = improved.Select(st => new Station
                {
                    position = st.position,
                    visibleGrades = st.visibleGrades != null ? new HashSet<Transform>(st.visibleGrades) : GetVisibleGradesFromPos(st.position)
                }).ToList();
                trial.Add(new Station { position = candidate, visibleGrades = visible });

                double trialCond = CalculatePlanConditionNumberForSolution(trial);
                if (trialCond < bestCond)
                {
                    bestCond = trialCond;
                    bestPos = candidate;
                    bestVisible = visible;
                }
            }

            if (bestPos == null || bestVisible == null)
                break;

            improved.Add(new Station { position = bestPos.Value, visibleGrades = bestVisible });
            improved = EnsureClosedTraverseConnectivity(improved, candidatePositions, bounds);
            currentCond = CalculatePlanConditionNumberForSolution(improved);
            Debug.Log($"Добавлена станция для улучшения обусловленности: cond={currentCond:N0}");
        }

        if (currentCond > ExcellentConditionThreshold)
            Debug.LogWarning($"Не удалось довести число обусловленности до отличного: cond={currentCond:N0}, целевое < {ExcellentConditionThreshold:N0}");

        return OrderStationsAsClosedTraverse(improved, bounds);
    }

    private double CalculatePlanConditionNumberForSolution(List<Station> solution)
    {
        if (solution == null || grades == null || grades.Count == 0)
            return double.PositiveInfinity;

        int n = grades.Count * 2;
        double[,] normal = new double[n, n];
        double sigmaDist = Math.Max(1e-6, DistanceStdevMm / 1000.0);
        double wDist = 1.0 / (sigmaDist * sigmaDist);
        double sigmaAngRad = Math.Max(1e-12, (DirectionStdevCc / 100.0) * (Math.PI / (180.0 * 3600.0)));
        double wAng = 1.0 / (sigmaAngRad * sigmaAngRad);

        for (int si = 0; si < solution.Count; si++)
        {
            var station = solution[si];
            if (station == null)
                continue;

            if (station.visibleGrades == null || station.visibleGrades.Count == 0)
                station.visibleGrades = GetVisibleGradesFromPos(station.position);

            foreach (var grade in station.visibleGrades)
            {
                int gi = grades.IndexOf(grade);
                if (gi < 0)
                    continue;

                Vector3 d = grade.position - station.position;
                double dx = d.x;
                double dz = d.z;
                double r = Math.Sqrt(dx * dx + dz * dz);
                if (r < 1e-6)
                    continue;

                int idx = gi * 2;

                double[] distanceRow = new double[n];
                distanceRow[idx] = dx / r;
                distanceRow[idx + 1] = dz / r;
                AccumulateRowToN(normal, distanceRow, wDist);

                double r2 = r * r;
                double[] directionRow = new double[n];
                directionRow[idx] = dz / r2;
                directionRow[idx + 1] = -dx / r2;
                AccumulateRowToN(normal, directionRow, wAng);
            }
        }

        const double regularization = 1e-9;
        for (int i = 0; i < n; i++)
            normal[i, i] += regularization;

        if (!JacobiEigenDecomposition(normal, out double[] eigenValues, out _))
            return double.PositiveInfinity;

        double maxEig = eigenValues.Max();
        double minEig = eigenValues
            .Where(v => v > regularization * 10.0)
            .DefaultIfEmpty(0.0)
            .Min();

        if (minEig <= 0.0 || maxEig <= 0.0)
            return double.PositiveInfinity;

        return maxEig / minEig;
    }

    // Расчет приспособленности решения
    private float CalculateFitness(List<Station> solution, Bounds bounds, Collider buildingCollider)
    {
        const float DEAD_PENALTY = -1e9f;

        // ================== 1. БАЗОВЫЕ ПРОВЕРКИ ==================
        if (solution == null || solution.Count < minStations)
            return DEAD_PENALTY;

        foreach (var station in solution)
        {
            if (IsInsideAnyBuilding(station.position))
                return DEAD_PENALTY;
        }

        // ================== 2. ПРОВЕРКА НА СЛИШКОМ БЛИЗКОЕ РАСПОЛОЖЕНИЕ ==================
        float minAllowedDistance = 8f; // реалистичное значение
        for (int i = 0; i < solution.Count; i++)
        {
            for (int j = i + 1; j < solution.Count; j++)
            {
                float distance = Vector3.Distance(solution[i].position, solution[j].position);
                if (distance < minAllowedDistance)
                {
                    return DEAD_PENALTY;
                }
            }
        }

        // ================== 3. ОБНОВЛЕНИЕ ВИДИМОСТИ ==================
        HashSet<Transform> coveredGrades = new HashSet<Transform>();
        Dictionary<Transform, int> gradeObservationCount = new Dictionary<Transform, int>();

        foreach (var grade in grades)
        {
            gradeObservationCount[grade] = 0;
        }

        foreach (var station in solution)
        {
            station.visibleGrades = GetVisibleGradesFromPos(station.position);
            if (station.visibleGrades == null || station.visibleGrades.Count == 0)
                return DEAD_PENALTY;

            coveredGrades.UnionWith(station.visibleGrades);

            foreach (var grade in station.visibleGrades)
            {
                if (gradeObservationCount.ContainsKey(grade))
                    gradeObservationCount[grade]++;
            }
        }

        // ================== 4. ПРОВЕРКА РЕДУНДАНТНОСТИ (минимум 2 наблюдения на марку) ==================
        int wellObservedGrades = 0;
        foreach (var count in gradeObservationCount.Values)
        {
            if (count >= 2)
                wellObservedGrades++;
        }

        float redundancyRatio = (float)wellObservedGrades / grades.Count;
        int extraGradeObservations = gradeObservationCount.Values.Sum(count => Mathf.Max(0, count - 2));

        // ЖЁСТКОЕ ТРЕБОВАНИЕ: все марки должны быть видны из ≥2 станций
        if (redundancyRatio < 1.0f)
        {
            float partialFitness = redundancyRatio * 50000f;
            partialFitness -= (1.0f - redundancyRatio) * 20000f;
            partialFitness -= solution.Count * 3000f;
            return partialFitness;
        }

        // ================== 5. ГЛАВНЫЙ ФИТНЕС: ПОЛНОЕ ПОКРЫТИЕ + РЕДУНДАНТНОСТЬ ==================
        float fitness = 100000f;

        // На одну марку достаточно двух наблюдений. Дополнительные наблюдения не запрещены,
        // но сильно штрафуются, чтобы алгоритм не плодил лишние станции.
        fitness -= extraGradeObservations * 12000f;

        // Поощряем конфигурации, где отдельная станция "берёт" максимум марок,
        // и где станции стоят дальше от марок (в пределах реалистичной дальности).
        int maxSingleStationCoverage = 0;
        float totalNearestDistance = 0f;
        foreach (var station in solution)
        {
            maxSingleStationCoverage = Mathf.Max(maxSingleStationCoverage, station.visibleGrades.Count);

            float nearest = float.MaxValue;
            foreach (var g in grades)
            {
                nearest = Mathf.Min(nearest, Vector3.Distance(station.position, g.position));
            }

            if (nearest < float.MaxValue)
                totalNearestDistance += nearest;
        }

        if (grades.Count > 0)
            fitness += (maxSingleStationCoverage / (float)grades.Count) * 35000f;

        if (solution.Count > 0)
        {
            float avgNearestDistance = totalNearestDistance / solution.Count;
            // Нормируем в диапазон 0..1, целевая "дальняя" дистанция ~20м.
            float distanceFactor = Mathf.Clamp01(avgNearestDistance / 20f);
            fitness += distanceFactor * 18000f;
        }

        // ================== 6. МИНИМИЗАЦИЯ КОЛИЧЕСТВА СТАНЦИЙ ==================
        int optimalCount = Mathf.Max(minStations, Mathf.CeilToInt(grades.Count / 2.0f));

        if (solution.Count > optimalCount)
        {
            int excessStations = solution.Count - optimalCount;
            fitness -= excessStations * 60000f;
        }
        else if (solution.Count < optimalCount)
        {
            fitness += (optimalCount - solution.Count) * 10000f;
        }

        // ================== 7. ПРОВЕРКА УГЛОВ НАБЛЮДЕНИЯ (3D) ==================
        int marksWithGoodAngles = 0;
        float totalMinAngle = 0f;

        foreach (var grade in grades)
        {
            var observingStations = solution
                .Where(s => s.visibleGrades.Contains(grade))
                .Select(s => s.position)
                .ToList();

            if (observingStations.Count < 2) continue;

            bool hasGoodAngle = false;
            float minAngle = 180f;

            for (int i = 0; i < observingStations.Count - 1; i++)
            {
                for (int j = i + 1; j < observingStations.Count; j++)
                {
                    Vector3 dir1 = (observingStations[i] - grade.position).normalized;
                    Vector3 dir2 = (observingStations[j] - grade.position).normalized;
                    float angle = Vector3.Angle(dir1, dir2);

                    minAngle = Mathf.Min(minAngle, angle);

                    // Максимум качества — около 90° (перпендикулярно к марке)
                    float perpendicularity = 1f - Mathf.Clamp01(Mathf.Abs(angle - 90f) / 90f);
                    fitness += perpendicularity * 2500f;

                    if (angle >= 70f && angle <= 110f)
                    {
                        hasGoodAngle = true;
                        fitness += 2500f;
                    }
                    else if (angle < 45f || angle > 135f)
                    {
                        // Избегаем острых/почти коллинеарных конфигураций
                        fitness -= 8000f;
                    }
                }
            }

            totalMinAngle += minAngle;
            if (hasGoodAngle) marksWithGoodAngles++;
        }

        if (grades.Count > 0)
        {
            float avgMinAngle = totalMinAngle / grades.Count;
            if (avgMinAngle >= 70f) fitness += 15000f;
            else if (avgMinAngle < 45f) fitness -= 20000f;
        }

        // ================== 8. РАСПРЕДЕЛЕНИЕ ПО КВАДРАНТАМ ==================
        if (solution.Count >= 3)
        {
            Vector3 center = bounds.center;
            int quadrantsOccupied = 0;
            bool[] quadrantOccupied = new bool[4];

            foreach (var station in solution)
            {
                Vector3 dir = station.position - center;
                int quad = (dir.x >= 0 ? 0 : 1) + (dir.z >= 0 ? 0 : 2);
                if (!quadrantOccupied[quad])
                {
                    quadrantOccupied[quad] = true;
                    quadrantsOccupied++;
                    fitness += 2000f;
                }
            }

            if (quadrantsOccupied == 4)
                fitness += 30000f;
            else if (quadrantsOccupied >= 3)
                fitness += 15000f;
            else if (quadrantsOccupied == 1)
                fitness -= 50000f;
        }

        // ================== 8.1. СТАНЦИИ ПО ОБЕИМ СТОРОНАМ ЗДАНИЯ ==================
        // Если марки расположены на двух противоположных сторонах фасада, сеть не должна
        // оставаться только на одной стороне: это ухудшает геометрию и видимость.
        fitness += CalculateTwoSidedStationScore(solution, bounds);

        // ================== 9. СВЯЗНОСТЬ И УНИКАЛЬНОСТЬ ==================
        int visiblePairs = 0;
        for (int i = 0; i < solution.Count; i++)
        {
            for (int j = i + 1; j < solution.Count; j++)
            {
                if (HasLineOfSight(solution[i].position, solution[j].position))
                    visiblePairs++;
            }
        }
        fitness += visiblePairs * 400f;
        if (visiblePairs >= solution.Count - 1)
            fitness += 10000f;

        // ================== 9.1. ПОЛИГОНОМЕТРИЯ ЗАМКНУТОГО ХОДА ==================
        // Станции сортируются по азимуту вокруг здания и должны образовывать видимый
        // замкнутый ход: T1-T2-...-Tn-T1. Такой критерий заставляет ГА строить
        // не произвольное облако точек, а связанный полигонометрический контур.
        int traverseLinks = CountClosedTraverseLinks(solution, bounds);
        float traverseRatio = solution.Count > 0 ? traverseLinks / (float)solution.Count : 0f;
        fitness += traverseRatio * 120000f;

        if (traverseLinks == solution.Count)
            fitness += 250000f + CalculateClosedTraverseShapeScore(solution, bounds);
        else
            fitness -= (solution.Count - traverseLinks) * 60000f;

        // Штраф за дублирующее покрытие
        for (int i = 0; i < solution.Count; i++)
        {
            for (int j = i + 1; j < solution.Count; j++)
            {
                int common = solution[i].visibleGrades.Intersect(solution[j].visibleGrades).Count();
                int total = Mathf.Max(solution[i].visibleGrades.Count, solution[j].visibleGrades.Count);
                if (total > 0 && common > total * 0.8f)
                    fitness -= 3000f;
            }
        }

        // ================== 9.2. ЧИСЛО ОБУСЛОВЛЕННОСТИ ==================
        double planCondition = CalculatePlanConditionNumberForSolution(solution);
        if (double.IsInfinity(planCondition) || double.IsNaN(planCondition))
        {
            fitness -= 250000f;
        }
        else if (planCondition <= ExcellentConditionThreshold)
        {
            // Требуем именно "отличную" геометрию по шкале отчёта: cond < 1000.
            fitness += 300000f;
            fitness += (float)Mathf.Clamp01((float)(1.0 - planCondition / ExcellentConditionThreshold)) * 100000f;
        }
        else
        {
            float conditionPenalty = Mathf.Clamp((float)Math.Log10(planCondition / ExcellentConditionThreshold + 1.0), 0f, 6f);
            fitness -= conditionPenalty * 90000f;
        }

        // ================== 10. ФИНАЛЬНЫЙ ШТРАФ/БОНУС ==================
        fitness -= solution.Count * 8000f;
        if (solution.Count <= optimalCount)
            fitness += 50000f;

        return fitness;
    }


    // Турнирный отбор
    private List<Station> TournamentSelection(List<(List<Station> solution, float fitness)> fitnessScores, int tournamentSize = 3)
    {
        if (fitnessScores.Count == 0) return new List<Station>();

        List<Station> best = null;
        float bestFitness = float.MinValue;

        // ПРОСТОЙ ТУРНИР: выбираем случайные решения и берем лучшее
        for (int i = 0; i < tournamentSize; i++)
        {
            int idx = UnityEngine.Random.Range(0, fitnessScores.Count);
            if (fitnessScores[idx].fitness > bestFitness)
            {
                bestFitness = fitnessScores[idx].fitness;
                best = fitnessScores[idx].solution;
            }
        }

        // Возвращаем копию лучшего решения
        if (best != null)
        {
            return best.Select(s => new Station
            {
                position = s.position,
                visibleGrades = new HashSet<Transform>(s.visibleGrades)
            }).ToList();
        }

        return new List<Station>();
    }

    // Скрещивание (объединение станций из родителей)
    private List<Station> Crossover(List<Station> parent1, List<Station> parent2, Bounds bounds, Collider buildingCollider)
    {
        if (parent1 == null || parent2 == null || parent1.Count == 0 || parent2.Count == 0)
            return new List<Station>();

        // ПРОСТОЙ КРОССОВЕР: объединяем все станции из родителей
        var child = new List<Station>();

        // Добавляем все станции из первого родителя
        foreach (var station in parent1)
        {
            if (station != null && station.visibleGrades != null && station.visibleGrades.Count > 0)
            {
                child.Add(new Station
                {
                    position = station.position,
                    visibleGrades = new HashSet<Transform>(station.visibleGrades)
                });
            }
        }

        // Добавляем уникальные станции из второго родителя
        foreach (var station in parent2)
        {
            if (station == null || station.visibleGrades == null || station.visibleGrades.Count == 0)
                continue;

            // Проверяем, не слишком ли близка эта станция к уже существующим
            bool isTooClose = false;
            foreach (var existing in child)
            {
                if (Vector3.Distance(existing.position, station.position) < 3f)
                {
                    isTooClose = true;
                    break;
                }
            }

            if (!isTooClose)
            {
                child.Add(new Station
                {
                    position = station.position,
                    visibleGrades = new HashSet<Transform>(station.visibleGrades)
                });
            }
        }

        // УДАЛИТЬ лишние станции если их слишком много (но оставить минимум)
        if (child.Count > 10)
        {
            child = child.OrderByDescending(s => s.visibleGrades.Count)
                        .Take(10)
                        .ToList();
        }

        // УДАЛИТЬ станции которые не видят ни одной марки
        child.RemoveAll(s => s.visibleGrades == null || s.visibleGrades.Count == 0);

        // ЕСЛИ станций меньше минимального, добавим случайные
        if (child.Count < minStations)
        {
            var candidates = GenerateCandidatePositions(bounds, buildingCollider, CalculateOptimalDistance(bounds));
            int needed = minStations - child.Count;

            for (int i = 0; i < needed && candidates.Count > 0; i++)
            {
                var pos = candidates[UnityEngine.Random.Range(0, candidates.Count)];
                var visible = GetVisibleGradesFromPos(pos);
                if (visible.Count > 0)
                {
                    child.Add(new Station { position = pos, visibleGrades = visible });
                    candidates.Remove(pos);
                }
            }
        }

        return child;
    }

    // Мутация
    private List<Station> Mutate(List<Station> solution, float mutationRate, Bounds bounds, Collider buildingCollider, float baseOffset)
    {
        if (solution == null || solution.Count == 0) return new List<Station>();

        var mutated = solution.Select(s => new Station
        {
            position = s.position,
            visibleGrades = new HashSet<Transform>(s.visibleGrades)
        }).ToList();

        // ПРОСТАЯ МУТАЦИЯ: с вероятностью 30% меняем случайную станцию
        if (UnityEngine.Random.value < mutationRate && mutated.Count > 0)
        {
            int idx = UnityEngine.Random.Range(0, mutated.Count);
            var stationToMutate = mutated[idx];

            // Просто немного сдвигаем позицию
            Vector3 shift = new Vector3(
                UnityEngine.Random.Range(-2f, 2f),
                0f,
                UnityEngine.Random.Range(-2f, 2f)
            );

            Vector3 newPos = stationToMutate.position + shift;

            // Проверяем, что новая позиция валидна
            if (!IsInsideAnyBuilding(newPos))
            {
                var newVisible = GetVisibleGradesFromPos(newPos);
                if (newVisible.Count > 0)
                {
                    mutated[idx].position = newPos;
                    mutated[idx].visibleGrades = newVisible;
                }
            }
        }

        // ВТОРАЯ МУТАЦИЯ: с вероятностью 20% удаляем случайную станцию (если останется минимум)
        if (UnityEngine.Random.value < 0.2f && mutated.Count > minStations)
        {
            int idx = UnityEngine.Random.Range(0, mutated.Count);
            mutated.RemoveAt(idx);
        }

        // ТРЕТЬЯ МУТАЦИЯ: с вероятностью 15% добавляем новую станцию
        if (UnityEngine.Random.value < 0.15f && mutated.Count < 12)
        {
            var candidates = GenerateCandidatePositions(bounds, buildingCollider, baseOffset);
            if (candidates.Count > 0)
            {
                var newPos = candidates[UnityEngine.Random.Range(0, candidates.Count)];
                var visible = GetVisibleGradesFromPos(newPos);
                if (visible.Count > 0)
                {
                    mutated.Add(new Station { position = newPos, visibleGrades = visible });
                }
            }
        }

        return mutated;
    }

    // Резервное решение если генетический алгоритм не сработал
    private List<Station> GenerateFallbackSolution(Bounds bounds, Collider buildingCollider, float baseOffset)
    {
        Debug.LogWarning("⚠️ Используется резервное решение");

        var solution = new List<Station>();
        var uncoveredGrades = new HashSet<Transform>(grades);
        var candidatePositions = GenerateCandidatePositions(bounds, buildingCollider, baseOffset);

        Debug.Log($"[FALLBACK] Кандидатов: {candidatePositions.Count}, марок: {uncoveredGrades.Count}");

        // Жадный алгоритм: добавляем станции пока не покроем все марки
        while (uncoveredGrades.Count > 0 && candidatePositions.Count > 0)
        {
            Vector3 bestPos = candidatePositions[0];
            var bestCoverage = GetVisibleGradesFromPos(bestPos);
            int bestCoveredCount = bestCoverage.Count(g => uncoveredGrades.Contains(g));

            foreach (var pos in candidatePositions.Skip(1))
            {
                var coverage = GetVisibleGradesFromPos(pos);
                int coveredCount = coverage.Count(g => uncoveredGrades.Contains(g));
                if (coveredCount > bestCoveredCount)
                {
                    bestPos = pos;
                    bestCoverage = coverage;
                    bestCoveredCount = coveredCount;
                }
            }

            if (bestCoveredCount > 0)
            {
                solution.Add(new Station { position = bestPos, visibleGrades = bestCoverage });
                foreach (var grade in bestCoverage)
                {
                    uncoveredGrades.Remove(grade);
                }
                Debug.Log($"[FALLBACK] Добавлена станция, покрыто {bestCoveredCount} новых марок " +
                         $"(осталось: {uncoveredGrades.Count})");
            }
            else
            {
                Debug.LogWarning($"[FALLBACK] Не найдено полезных кандидатов (осталось {uncoveredGrades.Count} марок)");
                break;
            }

            candidatePositions.Remove(bestPos);

            // ✅ Увеличена защита от бесконечного цикла
            if (solution.Count > 20) break;
        }

        if (uncoveredGrades.Count == 0)
        {
            Debug.Log($"✅ Fallback решение покрыло ВСЕ {grades.Count} марок!");
        }
        else
        {
            Debug.LogError($"❌ Fallback не смог покрыть {uncoveredGrades.Count} марок: " +
                          string.Join(", ", uncoveredGrades.Select(g => g.name)));
        }

        return solution;
    }

    // Размещение станции
    private bool TryPlaceStation(Station station, Collider buildingCollider)
    {
        if (station == null) return false;

        Vector3 spawn = station.position;
        Bounds bounds = GetObjectBounds(targetBuilding);

        // 1. ПРОВЕРКА НА НАХОЖДЕНИЕ ВНУТРИ ЗДАНИЯ
        if (IsInsideAnyBuilding(spawn))
        {
            Debug.LogError($"❌ ОТКЛОНЕНО: Станция внутри здания! Позиция: {spawn}");
            return false;
        }

        // 2. Проверка расстояния до стен здания
        Vector3 closestPoint = buildingCollider.ClosestPoint(spawn);
        float distanceToWall = Vector3.Distance(spawn, closestPoint);

        // Минимальное расстояние до стены (увеличим для безопасности)
        float minWallDistance = 2.0f;
        if (distanceToWall < minWallDistance)
        {
            Debug.LogWarning($"Станция слишком близко к стене! Расстояние: {distanceToWall:F1}m");

            // Пытаемся отодвинуть станцию от стены
            Vector3 awayFromWall = (spawn - closestPoint).normalized;
            Vector3 adjustedPos = spawn + awayFromWall * (minWallDistance - distanceToWall);

            // Проверяем новую позицию
            if (!IsInsideAnyBuilding(adjustedPos))
            {
                spawn = adjustedPos;
                Debug.Log($"✓ Станция отодвинута от стены. Новое расстояние: {Vector3.Distance(adjustedPos, closestPoint):F1}m");
            }
            else
            {
                return false;
            }
        }

        // 3. Проверка что точка находится на земле
        float rayStart = Mathf.Max(bounds.max.y + 10f, spawn.y + 10f);
        if (!Physics.Raycast(spawn + Vector3.up * rayStart, Vector3.down,
            out RaycastHit hit, rayStart + 50f, groundLayer))
        {
            Debug.LogWarning($"Не удалось найти землю под позицией: {spawn}");
            return false;
        }

        // Корректируем позицию на высоту земли
        spawn = hit.point;

        // 4. Финальная проверка после коррекции высоты
        if (IsInsideAnyBuilding(spawn))
        {
            Debug.LogError($"❌ ОТКЛОНЕНО: Станция внутри здания после коррекции высоты!");
            return false;
        }

        // 5. Проверка расстояния до других станций
        float minStationDistance = 5f;
        foreach (var existingStation in selectedStations)
        {
            if (Vector3.Distance(existingStation.position, spawn) < minStationDistance)
            {
                Debug.LogWarning($"Станция слишком близко к существующей станции!");
                return false;
            }
        }

        // 6. Создаём объект
        Vector3 spawnFinal = spawn + Vector3.up * 0.02f;
        GameObject obj = Instantiate(networkPrefab, spawnFinal, Quaternion.identity, parentContainer);
        station.networkObj = obj;
        station.ms60 = FindMS60Transform(obj);
        station.position = spawn;
        station.visibleGrades = GetVisibleGradesFromPos(spawn);
        selectedStations.Add(station);

        Debug.Log($"✅ Станция размещена на позиции: {spawn}");
        return true;
    }

    // ДОПОЛНИТЕЛЬНЫЙ метод: проверка через физику с учетом всех объектов с тегом building
    // Замените метод IsInsideAnyBuilding на этот:
    private bool IsInsideAnyBuilding(Vector3 pos)
    {
        // Быстрая проверка через OverlapSphere
        float checkRadius = 0.5f;
        Collider[] colliders = Physics.OverlapSphere(pos, checkRadius);

        foreach (Collider col in colliders)
        {
            // Проверяем все объекты с тегом building
            if (col.CompareTag("building"))
            {
                return true;
            }
        }

        // Дополнительная проверка через raycast вверх
        // Если луч пересекает здание нечетное число раз - точка внутри
        Ray ray = new Ray(pos + Vector3.down * 100f, Vector3.up);
        RaycastHit[] hits = Physics.RaycastAll(ray, 200f);

        int buildingHitCount = 0;
        foreach (RaycastHit hit in hits)
        {
            if (hit.collider.CompareTag("building") ||
                (hit.collider.transform.parent != null && hit.collider.transform.parent.CompareTag("building")))
            {
                buildingHitCount++;
            }
        }

        return buildingHitCount % 2 == 1; // Нечетное число попаданий = внутри здания
    }


    // Проверка прямой видимости между точками
    private bool HasLineOfSight(Vector3 fromPos, Vector3 toPos)
    {
        float maxDistance = 100f;
        float startTolerance = 0.2f;
        float endTolerance = 0.35f;

        Vector3 dir = toPos - fromPos;
        float dist = dir.magnitude;

        if (dist > maxDistance) return false;
        if (dist <= 0.001f) return true;

        // Поднимаем луч чуть выше земли, чтобы избежать ложных срабатываний
        Vector3 fromAdjusted = fromPos + Vector3.up * 0.1f;
        Vector3 toAdjusted = toPos + Vector3.up * 0.1f;
        dir = toAdjusted - fromAdjusted;
        dist = dir.magnitude;

        // Проверяем ВСЕ попадания, чтобы не пропускать здания, если они не входят в obstacleLayer.
        // Далее вручную оставляем только реальные препятствия или коллайдеры зданий.
        RaycastHit[] hits = Physics.RaycastAll(
            fromAdjusted,
            dir.normalized,
            dist,
            ~0,
            QueryTriggerInteraction.Ignore);

        foreach (RaycastHit hit in hits)
        {
            if (hit.collider == null)
                continue;

            // Игнорируем попадания слишком близко к станции или к самой цели.
            // Это устраняет случай, когда луч касается геометрии марки/фасада у конца.
            if (hit.distance <= startTolerance)
                continue;

            if (dist - hit.distance <= endTolerance)
                continue;

            bool isBuilding =
                hit.collider.CompareTag("building") ||
                (hit.collider.transform.parent != null && hit.collider.transform.parent.CompareTag("building"));

            bool isInObstacleMask = ((obstacleLayer.value & (1 << hit.collider.gameObject.layer)) != 0);
            if (!isBuilding && !isInObstacleMask)
                continue;

            return false;
        }

        return true;
    }


    // Получение видимых марок с позиции
    private HashSet<Transform> GetVisibleGradesFromPos(Vector3 pos)
    {
        HashSet<Transform> visible = new HashSet<Transform>();
        Vector3 from = pos + Vector3.up * 1.7f;

        foreach (var g in grades)
        {
            Vector3 to = g.position + Vector3.up * 0.5f;

            // Быстрая проверка расстояния
            float distance = Vector3.Distance(from, to);
            if (distance > 80f) continue; // Слишком далеко

            // Проверка угла (не слишком крутой угол)
            Vector3 horizontalDir = new Vector3(to.x - from.x, 0, to.z - from.z);
            float horizontalDist = horizontalDir.magnitude;
            float verticalDiff = Mathf.Abs(to.y - from.y);

            // Если угол слишком крутой (> 60 градусов), пропускаем
            if (horizontalDist > 0 && verticalDiff / horizontalDist > Mathf.Tan(60f * Mathf.Deg2Rad))
                continue;

            if (HasLineOfSight(from, to))
            {
                visible.Add(g);
            }
        }

        return visible;
    }
    private void DebugStationCoverage(List<Station> solution)
    {
        Debug.Log("=== ОТЛАДКА ПОКРЫТИЯ СТАНЦИЙ ===");

        for (int i = 0; i < solution.Count; i++)
        {
            var station = solution[i];
            Debug.Log($"Станция {i + 1} в {station.position}: видит {station.visibleGrades.Count} марок");

            foreach (var grade in station.visibleGrades)
            {
                Debug.Log($"  - {grade.name}");
            }
        }

        // Общее покрытие
        var allCovered = new HashSet<Transform>();
        foreach (var station in solution)
        {
            allCovered.UnionWith(station.visibleGrades);
        }
        Debug.Log($"ОБЩЕЕ ПОКРЫТИЕ: {allCovered.Count}/{grades.Count} марок");
    }


    // Получение границ объекта
    private Bounds GetObjectBounds(GameObject obj)
    {
        Renderer[] renderers = obj.GetComponentsInChildren<Renderer>();
        Bounds bounds = new Bounds(obj.transform.position, Vector3.zero);
        foreach (Renderer r in renderers) bounds.Encapsulate(r.bounds);
        return bounds;
    }


    // Проверка видимости к марке
    private bool CheckLineOfSightToGrade(Transform ms60, Transform grade)
    {
        if (ms60 == null || grade == null) return false;

        // Используем ту же высоту, что и в GetVisibleGradesFromPos
        Vector3 fromEyePos = ms60.position + Vector3.up * 1.7f;
        Vector3 toTargetPos = grade.position + Vector3.up * 0.5f;

        // Дополнительная проверка: линия должна быть выше минимальной высоты
        float minHeightAboveGround = 0.5f; // Минимум 0.5м над землей

        // Проверяем несколько точек вдоль линии
        int numChecks = 5;
        for (int i = 1; i < numChecks; i++)
        {
            float t = i / (float)numChecks;
            Vector3 checkPoint = Vector3.Lerp(fromEyePos, toTargetPos, t);

            // Проверяем, что точка не слишком близко к земле
            if (Physics.Raycast(checkPoint + Vector3.up * 10f, Vector3.down, out RaycastHit groundHit, 20f, groundLayer))
            {
                if (Vector3.Distance(checkPoint, groundHit.point) < minHeightAboveGround)
                {
                    return false; // Линия слишком близко к земле
                }
            }
        }

        return HasLineOfSight(fromEyePos, toTargetPos);
    }

    // Построение линии
    private void BuildLine(Vector3 start, Vector3 end, Color color, float width = 0.02f)
    {
        GameObject lineObj = new GameObject("Line");
        lineObj.transform.SetParent(parentContainer, true);

        LineRenderer lr = lineObj.AddComponent<LineRenderer>();
        lr.material = new Material(Shader.Find("Sprites/Default"));
        lr.startColor = lr.endColor = color;
        lr.startWidth = lr.endWidth = width;
        lr.positionCount = 2;
        lr.SetPosition(0, start);
        lr.SetPosition(1, end);
        lr.useWorldSpace = true;
    }


    // Поиск трансформа MS60
    private Transform FindMS60Transform(GameObject network)
    {
        if (network == null) return null;

        var ts = network.GetComponentsInChildren<Transform>(true)
            .FirstOrDefault(t => t.CompareTag("total_station"));
        if (ts != null) return ts;

        var exact = network.transform.Find("MS60 (1)") ?? network.transform.Find("MS60");
        if (exact != null) return exact;

        var any = network.GetComponentsInChildren<Transform>(true)
            .FirstOrDefault(t => t.name.ToLower().Contains("ms60"));
        if (any != null) return any;

        Debug.LogWarning($"Не найден объект MS60 в префабе {network.name}");
        return null;
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
            return 5.0f; // значение по умолчанию
        }
    }

    private void ExportXML()
    {
        UnityEngine.Random.InitState(12345);
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

        // === Первая станция T1 — начало координат (0,0) ===
        Vector3 origin = selectedStations[0].position;
        float coordNoise = CoordNoiseMm; // шум координат в мм, задаётся пользователем

        // === Экспорт станций ===
        for (int i = 0; i < selectedStations.Count; i++)
        {
            Vector3 relativePos = selectedStations[i].position - origin;

            // Убираем шум – используем точные координаты
            float noisyX = relativePos.x;
            float noisyZ = relativePos.z;

            if (i == 0)
            {
                noisyX = 0f;
                noisyZ = 0f;
            }

            xml.AppendLine(
                $"      <point id=\"T{i + 1}\" " +
                $"x=\"{noisyX.ToString("F3", CultureInfo.InvariantCulture)}\" " +
                $"y=\"{noisyZ.ToString("F3", CultureInfo.InvariantCulture)}\" fix=\"xy\" />"
            );
        }

        // === Экспорт марок как неизвестных (не задаём координаты) ===
        for (int g = 0; g < grades.Count; g++)
        {
            xml.AppendLine(
                $"      <point id=\"G{g + 1}\" adj=\"xy\" />"
            );
        }

        // === Подготовка списков для наблюдений ===
        var stationIds = selectedStations.Select((s, i) => $"T{i + 1}").ToList();
        var stationPoses = selectedStations.Select(s => s.ms60 != null ? s.ms60.position : s.position).ToList();

        var targetIds = new List<string>(stationIds);
        var targetPoses = new List<Vector3>(stationPoses);

        for (int g = 0; g < grades.Count; g++)
        {
            targetIds.Add($"G{g + 1}");
            targetPoses.Add(grades[g].position);
        }

        // === Экспорт наблюдений ===
        for (int s = 0; s < stationIds.Count; s++)
        {
            string fromId = stationIds[s];
            Vector3 fromPos = stationPoses[s];

            if (selectedStations[s].ms60 != null)
                fromPos = selectedStations[s].ms60.position;

            // ИСПРАВЛЕНИЕ: Проверяем, видит ли станция хоть что-то
            bool hasAnyVisibleTargets = false;

            // Сначала проверяем все цели
            var visibleTargets = new List<(string id, Vector3 pos)>();
            for (int t = 0; t < targetIds.Count; t++)
            {
                string toId = targetIds[t];
                if (toId == fromId) continue;

                Vector3 toPos = targetPoses[t];

                // ИСПРАВЛЕНИЕ: Используем ту же проверку видимости, что и в GetVisibleGradesFromPos
                // для консистентности
                Vector3 fromEyePos = fromPos + Vector3.up * 1.7f;
                Vector3 toTargetPos = toPos + Vector3.up * 0.5f;

                if (HasLineOfSight(fromEyePos, toTargetPos))
                {
                    visibleTargets.Add((toId, toPos));
                    hasAnyVisibleTargets = true;
                }
            }

            // Только если есть видимые цели, добавляем obs
            if (hasAnyVisibleTargets)
            {
                xml.AppendLine($"      <obs from=\"{fromId}\">");

                foreach (var target in visibleTargets)
                {
                    string toId = target.id;
                    Vector3 toPos = target.pos;

                    // Построение линии в Unity ТОЛЬКО для видимых целей
                    BuildLine(fromPos, toPos, Color.green, 0.02f);

                    // Расчет дирекционного угла
                    float dirDeg = CalculateAzimuthDegrees(fromPos, toPos);
                    string dirDms = ConvertDegreesToDMS(dirDeg);

                    // Расстояние между станцией и целью
                    float dx = toPos.x - fromPos.x;
                    float dz = toPos.z - fromPos.z;
                    float dist = Mathf.Sqrt(dx * dx + dz * dz);


                    // === Используем пользовательские stdev ===
                    xml.AppendLine(
                        $"        <direction to=\"{toId}\" val=\"{dirDms}\" stdev=\"{DirectionStdevCc}\" />"
                    );

                    xml.AppendLine(
                        $"        <distance to=\"{toId}\" val=\"{dist.ToString("F3", CultureInfo.InvariantCulture)}\" stdev=\"{DistanceStdevMm}\" />"
                    );
                }

                xml.AppendLine("      </obs>");
            }
            else
            {
                Debug.LogWarning($"Станция {fromId} не видит ни одной цели и будет пропущена в XML");
            }
        }

        xml.AppendLine("    </points-observations>");
        xml.AppendLine("  </network>");
        xml.AppendLine("</gama-local>");

        // === Сохранение XML ===
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
            return 5.0f; // значение по умолчанию, если поле пустое
        }
    }

    private float CalculateAzimuthDegrees(Vector3 from, Vector3 to)
    {
        Vector3 dir = to - from;
        float angle = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
        return (angle + 360f) % 360f;
    }

    [System.Serializable]
    private class PythonFiles { public string svg, txt, xml, html; }

    [System.Serializable]
    public class PythonResult
    {
        public string status;
        public string[] errors;
        public Outputs outputs;
    }
    [System.Serializable]
    public class Outputs
    {
        public string stdout;
        public string stderr;
        public string report;
    }

    // ==========================================================================
    //                    UI OUTPUT HELPERS (добавить в класс)
    // ==========================================================================

    private void AppendToNetworkReportUI(string message)
    {
        if (networkReportUIText != null)
        {
            networkReportUIText.text += message + "\n";

            // Автопрокрутка вниз через Canvas.ForceUpdateCanvases()
            Canvas.ForceUpdateCanvases();
        }
    }

    private void ClearNetworkReportUI()
    {
        if (networkReportUIText != null)
        {
            networkReportUIText.text = "";
        }
    }


    // ==========================================================================
    //                           NETWORK REPORT (ПОЛНЫЙ С UI)
    // ==========================================================================

    public void PrintNetworkReport()
    {
        // ✅ Очищаем предыдущий отчёт в UI
        ClearNetworkReportUI();

        if (grades == null || grades.Count == 0)
        {
            string msg = "Нет марок — отчёт невозможен.";
            Debug.LogWarning(msg);
            AppendToNetworkReportUI($"⚠️ {msg}");
            return;
        }

        if (selectedStations.Count < minStations)
        {
            string msg = $"Недостаточно станций: {selectedStations.Count} < {minStations}";
            Debug.LogWarning(msg);
            AppendToNetworkReportUI($"⚠️ {msg}");
            return;
        }

        // ===== 1. СЫРАЯ НОРМАЛЬНАЯ МАТРИЦА =====
        double[,] N_raw = BuildRawNormalMatrix();
        if (N_raw == null || N_raw.GetLength(0) == 0)
        {
            string msg = "Не удалось построить нормальную матрицу.";
            Debug.LogError(msg);
            AppendToNetworkReportUI($"❌ {msg}");
            return;
        }

        // ===== 2. СПЕКТРАЛЬНЫЙ АНАЛИЗ =====
        if (!JacobiEigenDecomposition(N_raw, out double[] eig_raw, out _))
        {
            string msg = "Не удалось выполнить спектральный анализ сырой матрицы.";
            Debug.LogError(msg);
            AppendToNetworkReportUI($"❌ {msg}");
            return;
        }

        Array.Sort(eig_raw);
        double threshold = eig_raw[eig_raw.Length - 1] * 1e-12;
        int zeroCount = 0;
        for (int i = 0; i < Math.Min(7, eig_raw.Length); i++)
            if (eig_raw[i] < threshold) zeroCount++;

        // ✅ Вывод в консоль и UI
        string spectrumHeader = "\n=== 📊 АНАЛИЗ СПЕКТРА СОБСТВЕННЫХ ЗНАЧЕНИЙ ===";
        Debug.Log(spectrumHeader);
        AppendToNetworkReportUI(spectrumHeader);

        string zeroCountMsg = $"Неопределённых степеней свободы: {zeroCount}/7";
        Debug.Log(zeroCountMsg);
        AppendToNetworkReportUI(zeroCountMsg);

        if (zeroCount > 0)
        {
            string warn = $"⚠️ СЕТЬ ИМЕЕТ {zeroCount} НЕОПРЕДЕЛЁННЫХ СТЕПЕНЕЙ СВОБОДЫ!";
            Debug.LogWarning(warn);
            AppendToNetworkReportUI(warn);
        }

        // ===== 3. ЧИСЛО ОБУСЛОВЛЕННОСТИ =====
        int startIdx = 7;
        double cond_geometric = double.PositiveInfinity;

        if (eig_raw.Length > startIdx)
        {
            double lambda_min = eig_raw[startIdx];
            double lambda_max = eig_raw[eig_raw.Length - 1];
            cond_geometric = (lambda_min < threshold) ? double.PositiveInfinity : lambda_max / lambda_min;
        }

        // ===== 4. КОВАРИАЦИОННАЯ МАТРИЦА =====
        if (!ComputeConditionNumberAndInverse(out _, out double[,] invN) || invN == null)
        {
            string msg = "Не удалось получить ковариационную матрицу (N⁻¹).";
            Debug.LogError(msg);
            AppendToNetworkReportUI($"❌ {msg}");
            return;
        }

        int n = invN.GetLength(0);

        // ===== 5. ОТЧЁТ =====
        string reportHeader = "\n=== 📐 ОТЧЁТ О ГЕОМЕТРИИ И ТОЧНОСТИ СЕТИ ===";
        Debug.Log(reportHeader);
        AppendToNetworkReportUI(reportHeader);

        Debug.Log($"Марок: {grades.Count}");
        AppendToNetworkReportUI($"🔹 Марок: {grades.Count}");

        Debug.Log($"Станций: {selectedStations.Count}");
        AppendToNetworkReportUI($"🔹 Станций: {selectedStations.Count}");

        string condMsg = $"🔹 Число обусловленности: {cond_geometric:N0}";
        Debug.Log($"Число обусловленности (геометрия сети): {cond_geometric:N0}");
        AppendToNetworkReportUI(condMsg);

        // Интерпретация
        string condStatus;
        if (double.IsInfinity(cond_geometric) || cond_geometric > 1e8)
        {
            condStatus = "❌ КАТАСТРОФИЧЕСКИ ПЛОХАЯ (вырожденная)";
            Debug.LogError("Геометрия сети: КАТАСТРОФИЧЕСКИ ПЛОХАЯ (вырожденная)");
        }
        else if (cond_geometric < 1e3)
        {
            condStatus = "✅ ОТЛИЧНАЯ";
            Debug.Log("Геометрия сети: ОТЛИЧНАЯ");
        }
        else if (cond_geometric < 5e3)
        {
            condStatus = "✅ ХОРОШАЯ";
            Debug.Log("Геометрия сети: ХОРОШАЯ");
        }
        else if (cond_geometric < 1e5)
        {
            condStatus = "⚠️ ДОПУСТИМАЯ (но требует внимания)";
            Debug.LogWarning("Геометрия сети: ДОПУСТИМАЯ (но требует внимания)");
        }
        else
        {
            condStatus = "❌ СЛАБАЯ (риск потери точности)";
            Debug.LogError("Геометрия сети: СЛАБАЯ (риск потери точности)");
        }
        AppendToNetworkReportUI($"🔹 Геометрия сети: {condStatus}");

        // ===== 6. ОЦЕНКА ТОЧНОСТИ =====
        string accuracyHeader = "\n=== 🎯 ОЦЕНКА ОТНОСИТЕЛЬНОЙ ТОЧНОСТИ (σ₀ = 1) ===";
        Debug.Log(accuracyHeader);
        AppendToNetworkReportUI(accuracyHeader);

        string accuracyNote = "⚠️ Для абсолютных ошибок требуется апостериорная дисперсия из невязок!";
        AppendToNetworkReportUI(accuracyNote);

        double maxSigma = 0, sumSigma = 0;
        int count = 0;
        int almostZeroPrintedCount = 0;
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

            string sxText = FormatSigmaMm(sx);
            string syText = FormatSigmaMm(sy);
            string szText = FormatSigmaMm(sz);
            string sigmaPlanText = FormatSigmaMm(sigmaPlan);
            string sigma3DText = FormatSigmaMm(sigma3D);

            if (sigma3D * 1000.0 < 0.05)
                almostZeroPrintedCount++;

            // ✅ Форматируем для консоли и UI
            string consoleLine = $"Марка {grades[i].name}: σX={sxText}, σY={syText}, σZ={szText}, σплан={sigmaPlanText}, σ3D={sigma3DText}";
            string uiLine = $"🔸 {grades[i].name}: σ3D={sigma3DText} (σплан={sigmaPlanText})";

            Debug.Log(consoleLine);
            AppendToNetworkReportUI(uiLine);
        }

        if (count > 0)
        {
            string avgMsg = $"\n📈 Средняя σ3D: {FormatSigmaMm(sumSigma / count)}";
            string maxMsg = $"📈 Максимальная σ3D: {FormatSigmaMm(maxSigma)}";

            Debug.Log($"\nСредняя σ3D: {FormatSigmaMm(sumSigma / count)}");
            Debug.Log($"Максимальная σ3D: {FormatSigmaMm(maxSigma)}");

            AppendToNetworkReportUI(avgMsg);
            AppendToNetworkReportUI(maxMsg);

            if (almostZeroPrintedCount == count)
            {
                string smallMsg = "⚠️ Все σ3D очень малы (<0.05 мм): это относительная оценка при σ₀=1";
                Debug.LogWarning("Все σ3D очень малы (<0.05 мм): это относительная оценка при σ₀=1 и текущих весах наблюдений, а не абсолютная апостериорная ошибка.");
                AppendToNetworkReportUI(smallMsg);
            }
        }

        // ===== 7. ПОКРЫТИЕ =====
        string coverageHeader = "\n=== 📡 АНАЛИЗ ПОКРЫТИЯ ===";
        Debug.Log(coverageHeader);
        AppendToNetworkReportUI(coverageHeader);

        var covered = new HashSet<Transform>();
        foreach (var st in selectedStations)
            if (st.visibleGrades != null)
                covered.UnionWith(st.visibleGrades);

        string coverageMsg = $"📡 Покрыто марок: {covered.Count}/{grades.Count}";
        Debug.Log($"Покрыто марок: {covered.Count}/{grades.Count}");
        AppendToNetworkReportUI(coverageMsg);

        var uncovered = grades.Where(g => !covered.Contains(g)).ToList();
        if (uncovered.Count > 0)
        {
            string uncoveredMsg = $"⚠️ Непокрытые марки: {string.Join(", ", uncovered.Select(g => g.name))}";
            Debug.LogWarning("Непокрытые марки: " + string.Join(", ", uncovered.Select(g => g.name)));
            AppendToNetworkReportUI(uncoveredMsg);
        }

        string endMsg = "\n=== ✅ ОТЧЁТ ЗАВЕРШЁН ===";
        Debug.Log(endMsg);
        AppendToNetworkReportUI(endMsg);
    }
    private string FormatSigmaMm(double sigmaMeters)
    {
        double mm = sigmaMeters * 1000.0;

        if (double.IsNaN(mm) || double.IsInfinity(mm))
            return "n/a";

        if (mm >= 0.05)
            return $"{mm:F1}мм";

        if (mm >= 0.0001)
            return $"{mm:F4}мм";

        return $"{mm:E2}мм";
    }

    // ==========================================================================
    //         NORMAL MATRIX + CONDITION NUMBER + PSEUDOINVERSE (3D, Unity)
    // ==========================================================================

    private bool ComputeConditionNumberAndInverse(out double condValue, out double[,] invN)
    {
        condValue = double.PositiveInfinity;
        invN = null;

        double[,] N = ComputeNormalMatrix(out bool isDegenerate);
        if (N == null || isDegenerate) return false;

        int n = N.GetLength(0);

        // спектральное разложение
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

        // формируем псевдообратную N⁻¹ = V * Λ⁻¹ * Vᵀ
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
    //                 NORMAL MATRIX BUILDER  (3D, Unity)
    // ==========================================================================

    private double[,] ComputeNormalMatrix(out bool isDegenerate)
    {
        isDegenerate = false;

        int nGrades = grades?.Count ?? 0;
        int nStations = selectedStations?.Count ?? 0;
        if (nGrades < 1 || nStations < 1) return null;

        int n = 3 * (nStations + nGrades);
        double[,] N = new double[n, n];

        // ===== ВЕСА ИЗМЕРЕНИЙ =====
        double sigmaDist = Math.Max(1e-6, DistanceStdevMm / 1000.0);
        double wDist = 1.0 / (sigmaDist * sigmaDist);

        // DirectionStdevCc — в санти-секундах (1 cc = 0.01 угл. сек)
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
                if (CheckLineOfSightToGrade(st.ms60, grades[gi]))
                    visible.Add((gi, grades[gi].position));
            }
            if (visible.Count < 1) continue;

            // === ИЗМЕРЕНИЯ РАССТОЯНИЙ ===
            foreach (var v in visible)
            {
                Vector3 d = v.p - S;
                double dx = d.x, dy = d.y, dz = d.z;
                double r = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (r < 1e-6) continue;

                int siIdx = si * 3;
                int giIdx = (nStations + v.idx) * 3;

                double[] row = new double[n];
                // Производные по станции
                row[siIdx + 0] = -dx / r;  // ∂r/∂Xₛ
                row[siIdx + 1] = -dy / r;  // ∂r/∂Yₛ
                row[siIdx + 2] = -dz / r;  // ∂r/∂Zₛ
                                           // Производные по марке
                row[giIdx + 0] = dx / r;   // ∂r/∂Xₘ
                row[giIdx + 1] = dy / r;   // ∂r/∂Yₘ
                row[giIdx + 2] = dz / r;   // ∂r/∂Zₘ

                AccumulateRowToN(N, row, wDist);
            }

            // === ИЗМЕРЕНИЯ УГЛОВ ===
            for (int i = 0; i < visible.Count - 1; i++)
            {
                for (int j = i + 1; j < visible.Count; j++)
                {
                    Vector3 p1 = visible[i].p - S;
                    Vector3 p2 = visible[j].p - S;

                    int siIdx = si * 3;
                    int gi1Idx = (nStations + visible[i].idx) * 3;
                    int gi2Idx = (nStations + visible[j].idx) * 3;

                    // --- ГОРИЗОНТАЛЬНЫЙ УГОЛ (в плоскости XZ) ---
                    double h1_sq = p1.x * p1.x + p1.z * p1.z;
                    double h2_sq = p2.x * p2.x + p2.z * p2.z;
                    if (h1_sq > 1e-12 && h2_sq > 1e-12)
                    {
                        double[] rowHor = new double[n];
                        // ∂α/∂X =  ΔZ / (ΔX²+ΔZ²), ∂α/∂Z = -ΔX / (ΔX²+ΔZ²)
                        rowHor[gi1Idx + 0] = p1.z / h1_sq;   // ∂α₁/∂X₁
                        rowHor[gi1Idx + 2] = -p1.x / h1_sq;  // ∂α₁/∂Z₁
                        rowHor[gi2Idx + 0] = -p2.z / h2_sq;  // ∂α₂/∂X₂
                        rowHor[gi2Idx + 2] = p2.x / h2_sq;   // ∂α₂/∂Z₂
                                                             // Станция
                        rowHor[siIdx + 0] = -rowHor[gi1Idx + 0] - rowHor[gi2Idx + 0];
                        rowHor[siIdx + 2] = -rowHor[gi1Idx + 2] - rowHor[gi2Idx + 2];
                        AccumulateRowToN(N, rowHor, wAng);
                    }

                    // --- ВЕРТИКАЛЬНЫЙ УГОЛ (угол подъёма v = arcsin(ΔY/r)) ---
                    double r1 = Math.Sqrt(p1.x * p1.x + p1.y * p1.y + p1.z * p1.z);
                    double r2 = Math.Sqrt(p2.x * p2.x + p2.y * p2.y + p2.z * p2.z);
                    double h1 = Math.Sqrt(h1_sq);
                    double h2 = Math.Sqrt(h2_sq);
                    if (r1 > 1e-6 && r2 > 1e-6 && h1 > 1e-6 && h2 > 1e-6)
                    {
                        double[] rowVer = new double[n];
                        // ∂v/∂X = -X·Y/(r²·h), ∂v/∂Y = h/r², ∂v/∂Z = -Z·Y/(r²·h)
                        double denom1 = r1 * r1 * h1;
                        rowVer[gi1Idx + 0] = -(p1.x * p1.y) / denom1;  // ∂v₁/∂X₁
                        rowVer[gi1Idx + 1] = h1 / (r1 * r1);           // ∂v₁/∂Y₁
                        rowVer[gi1Idx + 2] = -(p1.z * p1.y) / denom1;  // ∂v₁/∂Z₁

                        double denom2 = r2 * r2 * h2;
                        rowVer[gi2Idx + 0] = (p2.x * p2.y) / denom2;   // ∂v₂/∂X₂
                        rowVer[gi2Idx + 1] = -h2 / (r2 * r2);          // ∂v₂/∂Y₂
                        rowVer[gi2Idx + 2] = (p2.z * p2.y) / denom2;   // ∂v₂/∂Z₂

                        // Станция
                        rowVer[siIdx + 0] = -rowVer[gi1Idx + 0] - rowVer[gi2Idx + 0];
                        rowVer[siIdx + 1] = -rowVer[gi1Idx + 1] - rowVer[gi2Idx + 1];
                        rowVer[siIdx + 2] = -rowVer[gi1Idx + 2] - rowVer[gi2Idx + 2];

                        AccumulateRowToN(N, rowVer, wAng);
                    }
                }
            }
        }

        AddInterStationLinksToNormalMatrix(N, nStations, n, wDist);

        // ===== ПОЛНАЯ ФИКСАЦИЯ БАЗИСА (7 условий Хельмерта для Unity) =====
        if (nStations >= 1)
        {
            // Условия 1-3: устранение сдвигов (фиксируем первую станцию в начале координат)
            N[0, 0] += 1e12; // X₁ = 0
            N[1, 1] += 1e12; // Y₁ = 0
            N[2, 2] += 1e12; // Z₁ = 0
        }

        if (nStations >= 2)
        {
            // Условие 4: устранение поворота вокруг вертикали (Y)
            // Фиксируем Z-координату второй станции: Z₂ = Z₁ = 0
            N[5, 5] += 1e12; // Z₂ = 0

            // Условие 5: устранение поворота вокруг продольной оси (X)
            // Фиксируем Y-координату второй станции: Y₂ = Y₁ = 0
            N[4, 4] += 1e12; // Y₂ = 0

            // Условие 7: устранение масштабной неопределённости
            // Фиксируем расстояние между станциями 1 и 2
            Vector3 s1 = selectedStations[0].ms60.position;
            Vector3 s2 = selectedStations[1].ms60.position;
            double dx = s2.x - s1.x;
            double dy = s2.y - s1.y;
            double dz = s2.z - s1.z;
            double dist_sq = dx * dx + dy * dy + dz * dz;

            if (dist_sq > 1e-12)
            {
                double w_scale = 1e12 / dist_sq;

                // Станция 1 (индекс 0)
                N[0, 0] += w_scale * dx * dx;
                N[0, 1] += w_scale * dx * dy;
                N[0, 2] += w_scale * dx * dz;
                N[1, 0] += w_scale * dy * dx;
                N[1, 1] += w_scale * dy * dy;
                N[1, 2] += w_scale * dy * dz;
                N[2, 0] += w_scale * dz * dx;
                N[2, 1] += w_scale * dz * dy;
                N[2, 2] += w_scale * dz * dz;

                // Станция 2 (индекс 1)
                N[3, 3] += w_scale * dx * dx;
                N[3, 4] += w_scale * dx * dy;
                N[3, 5] += w_scale * dx * dz;
                N[4, 3] += w_scale * dy * dx;
                N[4, 4] += w_scale * dy * dy;
                N[4, 5] += w_scale * dy * dz;
                N[5, 3] += w_scale * dz * dx;
                N[5, 4] += w_scale * dz * dy;
                N[5, 5] += w_scale * dz * dz;

                // Смешанные производные (станция 1 ↔ станция 2)
                N[0, 3] -= w_scale * dx * dx;
                N[0, 4] -= w_scale * dx * dy;
                N[0, 5] -= w_scale * dx * dz;
                N[1, 3] -= w_scale * dy * dx;
                N[1, 4] -= w_scale * dy * dy;
                N[1, 5] -= w_scale * dy * dz;
                N[2, 3] -= w_scale * dz * dx;
                N[2, 4] -= w_scale * dz * dy;
                N[2, 5] -= w_scale * dz * dz;

                N[3, 0] -= w_scale * dx * dx;
                N[3, 1] -= w_scale * dx * dy;
                N[3, 2] -= w_scale * dx * dz;
                N[4, 0] -= w_scale * dy * dx;
                N[4, 1] -= w_scale * dy * dy;
                N[4, 2] -= w_scale * dy * dz;
                N[5, 0] -= w_scale * dz * dx;
                N[5, 1] -= w_scale * dz * dy;
                N[5, 2] -= w_scale * dz * dz;
            }
        }

        if (nStations >= 3)
        {
            // Условие 6: устранение поворота вокруг оси между ст.1 и ст.2
            // Так как ст.1=(0,0,0), ст.2=(X₂,0,0), то плоскость ст.1-ст.2 = XY
            // Фиксируем Z-координату третьей станции: Z₃ = 0
            N[8, 8] += 1e12; // Z₃ = 0
        }

        // ===== ЧИСЛЕННАЯ СТАБИЛИЗАЦИЯ (только для решения СЛАУ) =====
        const double regularization = 1e-10;
        for (int i = 0; i < n; i++)
            N[i, i] += regularization;

        // ===== ПРОВЕРКА ВЫРОЖДЕННОСТИ =====
        if (!JacobiEigenDecomposition(N, out double[] ev, out _))
        {
            isDegenerate = true;
            return N;
        }

        double minEig = ev.Min();
        isDegenerate = (minEig < regularization * 0.1);

        return N;
    }
    private double[,] BuildRawNormalMatrix()
    {
        int nGrades = grades?.Count ?? 0;
        int nStations = selectedStations?.Count ?? 0;
        if (nGrades < 1 || nStations < 1) return null;

        int n = 3 * (nStations + nGrades);
        double[,] N = new double[n, n];

        // ===== ВЕСА ИЗМЕРЕНИЙ =====
        double sigmaDist = Math.Max(1e-6, DistanceStdevMm / 1000.0);
        double wDist = 1.0 / (sigmaDist * sigmaDist);

        // DirectionStdevCc — в санти-секундах (1 cc = 0.01 угл. сек)
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
                if (CheckLineOfSightToGrade(st.ms60, grades[gi]))
                    visible.Add((gi, grades[gi].position));
            }
            if (visible.Count < 1) continue;

            // === ИЗМЕРЕНИЯ РАССТОЯНИЙ ===
            foreach (var v in visible)
            {
                Vector3 d = v.p - S;
                double dx = d.x, dy = d.y, dz = d.z;
                double r = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (r < 1e-6) continue;

                int siIdx = si * 3;
                int giIdx = (nStations + v.idx) * 3;

                double[] row = new double[n];
                row[siIdx + 0] = -dx / r;  // ∂r/∂Xₛ
                row[siIdx + 1] = -dy / r;  // ∂r/∂Yₛ
                row[siIdx + 2] = -dz / r;  // ∂r/∂Zₛ
                row[giIdx + 0] = dx / r;   // ∂r/∂Xₘ
                row[giIdx + 1] = dy / r;   // ∂r/∂Yₘ
                row[giIdx + 2] = dz / r;   // ∂r/∂Zₘ

                AccumulateRowToN(N, row, wDist);
            }

            // === ИЗМЕРЕНИЯ УГЛОВ ===
            for (int i = 0; i < visible.Count - 1; i++)
            {
                for (int j = i + 1; j < visible.Count; j++)
                {
                    Vector3 p1 = visible[i].p - S;
                    Vector3 p2 = visible[j].p - S;

                    int siIdx = si * 3;
                    int gi1Idx = (nStations + visible[i].idx) * 3;
                    int gi2Idx = (nStations + visible[j].idx) * 3;

                    // --- ГОРИЗОНТАЛЬНЫЙ УГОЛ (плоскость XZ) ---
                    double h1_sq = p1.x * p1.x + p1.z * p1.z;
                    double h2_sq = p2.x * p2.x + p2.z * p2.z;
                    if (h1_sq > 1e-12 && h2_sq > 1e-12)
                    {
                        double[] rowHor = new double[n];
                        rowHor[gi1Idx + 0] = p1.z / h1_sq;   // ∂α₁/∂X₁
                        rowHor[gi1Idx + 2] = -p1.x / h1_sq;   // ∂α₁/∂Z₁
                        rowHor[gi2Idx + 0] = -p2.z / h2_sq;   // ∂α₂/∂X₂
                        rowHor[gi2Idx + 2] = p2.x / h2_sq;   // ∂α₂/∂Z₂
                        rowHor[siIdx + 0] = -rowHor[gi1Idx + 0] - rowHor[gi2Idx + 0];
                        rowHor[siIdx + 2] = -rowHor[gi1Idx + 2] - rowHor[gi2Idx + 2];
                        AccumulateRowToN(N, rowHor, wAng);
                    }

                    // --- ВЕРТИКАЛЬНЫЙ УГОЛ (угол подъёма) ---
                    double r1 = Math.Sqrt(p1.x * p1.x + p1.y * p1.y + p1.z * p1.z);
                    double r2 = Math.Sqrt(p2.x * p2.x + p2.y * p2.y + p2.z * p2.z);
                    double h1 = Math.Sqrt(h1_sq);
                    double h2 = Math.Sqrt(h2_sq);
                    if (r1 > 1e-6 && r2 > 1e-6 && h1 > 1e-6 && h2 > 1e-6)
                    {
                        double[] rowVer = new double[n];
                        double denom1 = r1 * r1 * h1;
                        rowVer[gi1Idx + 0] = -(p1.x * p1.y) / denom1;  // ∂v₁/∂X₁
                        rowVer[gi1Idx + 1] = h1 / (r1 * r1);          // ∂v₁/∂Y₁
                        rowVer[gi1Idx + 2] = -(p1.z * p1.y) / denom1;  // ∂v₁/∂Z₁

                        double denom2 = r2 * r2 * h2;
                        rowVer[gi2Idx + 0] = (p2.x * p2.y) / denom2;  // ∂v₂/∂X₂
                        rowVer[gi2Idx + 1] = -h2 / (r2 * r2);          // ∂v₂/∂Y₂
                        rowVer[gi2Idx + 2] = (p2.z * p2.y) / denom2;  // ∂v₂/∂Z₂

                        rowVer[siIdx + 0] = -rowVer[gi1Idx + 0] - rowVer[gi2Idx + 0];
                        rowVer[siIdx + 1] = -rowVer[gi1Idx + 1] - rowVer[gi2Idx + 1];
                        rowVer[siIdx + 2] = -rowVer[gi1Idx + 2] - rowVer[gi2Idx + 2];

                        AccumulateRowToN(N, rowVer, wAng);
                    }
                }
            }
        }

        AddInterStationLinksToNormalMatrix(N, nStations, n, wDist);

        return N;
    }

    private void AddInterStationLinksToNormalMatrix(double[,] N, int nStations, int n, double weight)
    {
        if (N == null || nStations < 2) return;

        for (int si = 0; si < nStations - 1; si++)
        {
            var stA = selectedStations[si];
            if (stA?.ms60 == null) continue;

            Vector3 a = stA.ms60.position;
            int siIdx = si * 3;

            for (int sj = si + 1; sj < nStations; sj++)
            {
                var stB = selectedStations[sj];
                if (stB?.ms60 == null) continue;

                Vector3 b = stB.ms60.position;

                Vector3 d = b - a;
                double dx = d.x, dy = d.y, dz = d.z;
                double r = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (r < 1e-6) continue;

                int sjIdx = sj * 3;
                double[] row = new double[n];

                // Расстояние между станциями rij = |Sj - Si|
                row[siIdx + 0] = -dx / r;
                row[siIdx + 1] = -dy / r;
                row[siIdx + 2] = -dz / r;

                row[sjIdx + 0] = dx / r;
                row[sjIdx + 1] = dy / r;
                row[sjIdx + 2] = dz / r;

                AccumulateRowToN(N, row, weight);
            }
        }
    }

    // ==========================================================================
    //                JACOBI EIGEN DECOMPOSITION  (3D, SYMMETRIC)
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

    private Dictionary<(int, int), double> observedDistances = new();
    private Dictionary<(int, int, int), double> observedAngles = new();

    private void InitializeObservations()
    {
        // Жёсткая фиксация случайности (как в GNU Gama)
        UnityEngine.Random.InitState(12345);

        observedDistances.Clear();
        observedAngles.Clear();

        for (int si = 0; si < selectedStations.Count; si++)
        {
            var st = selectedStations[si];
            if (st?.ms60 == null) continue;

            Vector3 S = st.ms60.position;

            // === 1. Сбор видимых марок ===
            var visible = new List<(int idx, Vector3 pos)>();

            for (int gi = 0; gi < grades.Count; gi++)
            {
                if (grades[gi] == null) continue;
                if (!CheckLineOfSightToGrade(st.ms60, grades[gi])) continue;

                visible.Add((gi, grades[gi].position));
            }

            if (visible.Count == 0) continue;

            // =====================================================
            // === 2. РАССТОЯНИЯ (ТОЛЬКО 2D!)
            // =====================================================
            foreach (var v in visible)
            {
                Vector3 d = v.pos - S;

                double dx = d.x;
                double dz = d.z;

                double r = Math.Sqrt(dx * dx + dz * dz);
                if (r < 1e-6) continue;

                observedDistances[(si, v.idx)] = r;
            }

            // =====================================================
            // === 3. УГЛЫ (строго через направления!)
            // =====================================================
            for (int i = 0; i < visible.Count - 1; i++)
            {
                for (int j = i + 1; j < visible.Count; j++)
                {
                    Vector3 p1 = visible[i].pos - S;
                    Vector3 p2 = visible[j].pos - S;

                    // направления
                    double α1 = Math.Atan2(p1.x, p1.z);
                    double α2 = Math.Atan2(p2.x, p2.z);

                    if (α1 < 0) α1 += 2 * Math.PI;
                    if (α2 < 0) α2 += 2 * Math.PI;

                    double angle = α2 - α1;

                    if (angle < 0) angle += 2 * Math.PI;

                    // шум угла (в радианах!)
                    observedAngles[(si, visible[i].idx, visible[j].idx)] = angle;
                }
            }
        }

        Debug.Log($"Наблюдения зафиксированы: dist={observedDistances.Count}, angles={observedAngles.Count}");
    }

    // ==========================================================================
    //   ADJUSTMENT USING EXISTING ComputeNormalMatrix LOGIC (GNU Gama style)
    //   ДАННЫЕ: selectedStations/grades (как в ExportXML)
    //   ВЫВОД: координаты марок как в GNU: X=East, Y=North
    // ==========================================================================

    public void PerformAdjustmentUsingExistingMatrix(int maxIter = 1)
    {
        if (selectedStations.Count == 0 || grades.Count == 0)
        {
            Debug.LogError("Нет данных для уравнивания!");
            return;
        }

        UnityEngine.Random.InitState(12345); // фиксируем случайность
        InitializeObservations();            // создаём наблюдения ОДИН РАЗ

        Debug.Log("\n=== УРАВНИВАНИЕ СЕТИ (МНК, с итерациями) ===");

        int nStations = selectedStations.Count;
        int nGrades = grades.Count;

        // Сохраняем текущие координаты марок (будем их обновлять)
        Vector3[] gradePos = new Vector3[nGrades];
        for (int gi = 0; gi < nGrades; gi++)
            gradePos[gi] = grades[gi].position;

        for (int iter = 0; iter < maxIter; iter++)
        {
            // Обновляем позиции марок в сцене для вычисления невязок
            for (int gi = 0; gi < nGrades; gi++)
                grades[gi].position = gradePos[gi];

            var result = ComputeNormalMatrixWithMisclosures(out bool isDegenerate);
            if (result.N == null || isDegenerate)
            {
                Debug.LogError("Не удалось построить нормальную матрицу или сеть вырождена!");
                return;
            }

            double[,] N = result.N;
            double[] u = result.u;
            int gradeStartIdx = 3 * nStations;
            int nGradeParams = 3 * nGrades;

            // Извлекаем подматрицу для марок
            double[,] N_gg = new double[nGradeParams, nGradeParams];
            double[] u_g = new double[nGradeParams];
            for (int i = 0; i < nGradeParams; i++)
            {
                u_g[i] = u[gradeStartIdx + i];
                for (int j = 0; j < nGradeParams; j++)
                    N_gg[i, j] = N[gradeStartIdx + i, gradeStartIdx + j];
            }

            if (!SolveWithSVD(N_gg, u_g, out double[] dx_g))
            {
                Debug.LogError("Не удалось решить систему для марок!");
                return;
            }
            if (dx_g == null)
            {
                Debug.LogError("Не удалось решить систему для марок!");
                return;
            }

            double maxChange = 0;
            for (int gi = 0; gi < nGrades; gi++)
            {
                Vector3 change = new Vector3(
                    (float)dx_g[gi * 3 + 0],
                    (float)dx_g[gi * 3 + 1],
                    (float)dx_g[gi * 3 + 2]
                );
                gradePos[gi] += change;
                maxChange = Math.Max(maxChange, change.magnitude);
            }

            Debug.Log($"Итерация {iter + 1}: max |dx| = {maxChange * 1000:F4} мм");
            if (maxChange < 1e-6) break;
        }

        // Вывод результатов
        Vector3 origin = selectedStations[0].position;
        Debug.Log("\n=== РЕЗУЛЬТАТЫ: КООРДИНАТЫ МАРОК (сравнение с GNU Gama) ===");
        Debug.Log(new string('-', 55));
        Debug.Log($"{"Марка",-6} {"X (East), м",14} {"Y (North), м",14}");
        Debug.Log(new string('-', 55));
        for (int gi = 0; gi < nGrades; gi++)
        {
            Vector3 rel = gradePos[gi] - origin;
            Debug.Log($"G{gi + 1,-6} {rel.x,14:F5} {rel.z,14:F5}");
        }
        Debug.Log(new string('-', 55));
    }

    public void ComputeAndPrintAdjustedObservations()
    {
        Debug.Log("\n=== УРАВНЕННЫЕ НАБЛЮДЕНИЯ (L_adj = L_obs + v) ===");

        // ===== 1. ПОЛУЧАЕМ РЕЗУЛЬТАТЫ УРАВНИВАНИЯ =====
        var result = ComputeNormalMatrixWithMisclosures(out bool isDegenerate);
        if (result.N == null || isDegenerate) return;

        int nStations = selectedStations.Count;
        int nGrades = grades.Count;
        int gradeStartIdx = 3 * nStations;
        int nGradeParams = 3 * nGrades;

        // Извлекаем подматрицу для марок
        double[,] N_gg = new double[nGradeParams, nGradeParams];
        double[] u_g = new double[nGradeParams];
        for (int i = 0; i < nGradeParams; i++)
        {
            u_g[i] = result.u[gradeStartIdx + i];
            for (int j = 0; j < nGradeParams; j++)
                N_gg[i, j] = result.N[gradeStartIdx + i, gradeStartIdx + j];
        }

        // Решаем систему
        if (!SolveWithSVD(N_gg, u_g, out double[] dx_g)) return;
        if (dx_g == null) return;

        // ===== 2. ВЫЧИСЛЯЕМ УРАВНЕННЫЕ КООРДИНАТЫ МАРОК =====
        Vector3 origin = selectedStations[0].position;
        var adjustedMarks = new Dictionary<string, Vector3>();

        for (int gi = 0; gi < nGrades; gi++)
        {
            Vector3 origPos = grades[gi].position - origin;
            Vector3 adjPos = new Vector3(
                (float)(origPos.x + dx_g[gi * 3 + 0]),  // X = East
                (float)(origPos.y + dx_g[gi * 3 + 1]),  // Y = Height
                (float)(origPos.z + dx_g[gi * 3 + 2])   // Z = North
            );
            adjustedMarks[$"G{gi + 1}"] = adjPos;
        }

        // ===== 3. ВЫЧИСЛЯЕМ УРАВНЕННЫЕ НАБЛЮДЕНИЯ =====
        Debug.Log(new string('-', 90));
        Debug.Log($"{"Станция",-6} {"Цель",-6} {"Тип",-10} {"L_obs",12} {"v",10} {"L_adj",12} {"Статус"}");
        Debug.Log(new string('-', 90));

        double sumVTPV = 0;  // для вычисления σ₀
        int nObs = 0;

        foreach (var st in selectedStations)
        {
            string fromId = $"T{selectedStations.IndexOf(st) + 1}";
            Vector3 S = st.ms60 != null ? st.ms60.position : st.position;

            foreach (var grade in grades)
            {
                string toId = $"G{grades.IndexOf(grade) + 1}";
                if (!adjustedMarks.ContainsKey(toId)) continue;

                Vector3 G_adj = adjustedMarks[toId];

                // --- РАССТОЯНИЕ ---
                double dx = grade.position.x - S.x;
                double dz = grade.position.z - S.z;
                double dist_obs = Math.Sqrt(dx * dx + dz * dz);
                double dist_comp = Vector3.Distance(S, G_adj);
                double v_dist = dist_obs - dist_comp;  // поправка
                double dist_adj = dist_obs + v_dist;    // уравненное

                string distStatus = Math.Abs(v_dist) * 1000 < 1.0 ? "✓" :
                                   Math.Abs(v_dist) * 1000 < 5.0 ? "~" : "✗";

                Debug.Log(
                    $"{fromId,-6} {toId,-6} {"Dist",-10} " +
                    $"{dist_obs,12:F3} {v_dist,10:F3} {dist_adj,12:F3} {distStatus}");

                sumVTPV += v_dist * v_dist / Math.Pow(DistanceStdevMm / 1000.0, 2);
                nObs++;

                // --- НАПРАВЛЕНИЕ (азимут) ---
                double az_obs = CalculateAzimuthDegrees(S, grade.position) * Math.PI / 180.0;
                double az_comp = Math.Atan2(G_adj.x - (S.x - origin.x), G_adj.z - (S.z - origin.z));
                if (az_comp < 0) az_comp += 2 * Math.PI;

                double v_az = az_obs - az_comp;
                while (v_az > Math.PI) v_az -= 2 * Math.PI;
                while (v_az < -Math.PI) v_az += 2 * Math.PI;

                double az_adj = az_obs + v_az;
                if (az_adj < 0) az_adj += 2 * Math.PI;
                if (az_adj >= 2 * Math.PI) az_adj -= 2 * Math.PI;

                // Конвертируем в градусы для вывода
                double az_obs_deg = az_obs * 180 / Math.PI;
                double v_az_cc = v_az * 180 * 3600 / Math.PI;  // в санти-секундах
                double az_adj_deg = az_adj * 180 / Math.PI;

                string azStatus = Math.Abs(v_az_cc) < 10 ? "✓" :
                                 Math.Abs(v_az_cc) < 30 ? "~" : "✗";

                Debug.Log(
                    $"{fromId,-6} {toId,-6} {"Azimuth",-10} " +
                    $"{az_obs_deg,12:F2}° {v_az_cc,10:F1}cc {az_adj_deg,12:F2}° {azStatus}");

                double sigmaAng_rad = (DirectionStdevCc / 100.0) * Math.PI / (180.0 * 3600.0);
                sumVTPV += v_az * v_az / (sigmaAng_rad * sigmaAng_rad);
                nObs++;
            }
        }

        Debug.Log(new string('-', 90));

        // ===== 4. АПОСТЕРИОРНАЯ ОЦЕНКА ТОЧНОСТИ =====
        int nParams = nGradeParams;  // только координаты марок
        double dof = nObs - nParams; // число степеней свободы

        if (dof > 0)
        {
            double sigma0_aposteriori = Math.Sqrt(sumVTPV / dof);
            Debug.Log($"Апостериорная СКО единицы веса: σ₀ = {sigma0_aposteriori:F3}");
            Debug.Log($"Число наблюдений: {nObs}, параметров: {nParams}, степеней свободы: {dof}");

            if (sigma0_aposteriori < 0.8)
                Debug.Log("✓ Точность наблюдений лучше априорной");
            else if (sigma0_aposteriori < 1.2)
                Debug.Log("✓ Точность соответствует априорной");
            else
                Debug.LogWarning($"⚠ Точность хуже априорной (σ₀={sigma0_aposteriori:F2})");
        }

        Debug.Log("=== УРАВНЕННЫЕ НАБЛЮДЕНИЯ ЗАВЕРШЕНЫ ===\n");
    }

    private struct NormalMatrixResult
    {
        public double[,] N;  // Нормальная матрица
        public double[] u;   // Вектор невязок: Aᵀ * P * l

        public NormalMatrixResult(double[,] n, double[] misc)
        {
            N = n; u = misc;
        }
    }

    private NormalMatrixResult ComputeNormalMatrixWithMisclosures(out bool isDegenerate)
    {
        isDegenerate = false;

        int nGrades = grades?.Count ?? 0;
        int nStations = selectedStations?.Count ?? 0;
        if (nGrades < 1 || nStations < 1)
        {
            isDegenerate = true;
            return new NormalMatrixResult(null, null);
        }

        int n = 3 * (nStations + nGrades);
        double[,] N = new double[n, n];
        double[] u = new double[n];

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
                if (CheckLineOfSightToGrade(st.ms60, grades[gi]))
                    visible.Add((gi, grades[gi].position));
            }
            if (visible.Count < 1) continue;

            // === РАССТОЯНИЯ (горизонтальные) ===
            foreach (var v in visible)
            {
                Vector3 d = v.p - S;
                double dx = d.x, dz = d.z;
                double r_computed = Math.Sqrt(dx * dx + dz * dz);
                if (r_computed < 1e-6) continue;

                if (!observedDistances.TryGetValue((si, v.idx), out double r_observed))
                    continue;

                double misclosure = r_observed - r_computed;

                int siIdx = si * 3;
                int giIdx = (nStations + v.idx) * 3;

                double[] row = new double[n];
                row[siIdx + 0] = -dx / r_computed;
                row[siIdx + 2] = -dz / r_computed;
                row[giIdx + 0] = dx / r_computed;
                row[giIdx + 2] = dz / r_computed;
                // Производные по Y обнулены

                AccumulateRowToNAndU(N, u, row, misclosure, wDist);
            }

            // === ГОРИЗОНТАЛЬНЫЕ УГЛЫ (без изменений) ===
            for (int i = 0; i < visible.Count - 1; i++)
            {
                for (int j = i + 1; j < visible.Count; j++)
                {
                    Vector3 p1 = visible[i].p - S;
                    Vector3 p2 = visible[j].p - S;
                    double h1_sq = p1.x * p1.x + p1.z * p1.z;
                    double h2_sq = p2.x * p2.x + p2.z * p2.z;
                    if (h1_sq > 1e-12 && h2_sq > 1e-12)
                    {
                        double α1_comp = Math.Atan2(p1.x, p1.z);
                        double α2_comp = Math.Atan2(p2.x, p2.z);
                        if (α1_comp < 0) α1_comp += 2 * Math.PI;
                        if (α2_comp < 0) α2_comp += 2 * Math.PI;
                        double α1_obs = CalculateAzimuthDegrees(S, visible[i].p) * Math.PI / 180.0;
                        double α2_obs = CalculateAzimuthDegrees(S, visible[j].p) * Math.PI / 180.0;
                        if (!observedAngles.TryGetValue((si, visible[i].idx, visible[j].idx), out double obs))
                            continue;

                        double comp = α2_comp - α1_comp;
                        double misclosure = obs - comp;
                        while (misclosure > Math.PI) misclosure -= 2 * Math.PI;
                        while (misclosure < -Math.PI) misclosure += 2 * Math.PI;

                        int siIdx = si * 3;
                        int gi1Idx = (nStations + visible[i].idx) * 3;
                        int gi2Idx = (nStations + visible[j].idx) * 3;

                        double[] rowHor = new double[n];
                        rowHor[gi1Idx + 0] = p1.z / h1_sq;
                        rowHor[gi1Idx + 2] = -p1.x / h1_sq;
                        rowHor[gi2Idx + 0] = -p2.z / h2_sq;
                        rowHor[gi2Idx + 2] = p2.x / h2_sq;
                        rowHor[siIdx + 0] = -rowHor[gi1Idx + 0] - rowHor[gi2Idx + 0];
                        rowHor[siIdx + 2] = -rowHor[gi1Idx + 2] - rowHor[gi2Idx + 2];

                        AccumulateRowToNAndU(N, u, rowHor, misclosure, wAng);
                    }
                }
            }

            // === ВЕРТИКАЛЬНЫЕ УГЛЫ – УДАЛЕНЫ ===
        }

        // ===== ФИКСАЦИЯ ДАТУМА (без изменений) =====
        if (nStations >= 1)
        {
            for (int k = 0; k < 3; k++)
            {
                N[k, k] += 1e15;   // было 1e12
                u[k] += 0;
            }
        }

        // Фиксация ВСЕХ Y (вся сеть 2D)
        for (int i = 0; i < n; i += 3)
        {
            N[i + 1, i + 1] += 1e15;   // было 1e12
            u[i + 1] += 0;
        }

        if (nStations >= 2)
        {
            N[5, 5] += 1e15; N[4, 4] += 1e15;   // было 1e12
            u[5] += 1e15 * 0; u[4] += 1e15 * 0;

            Vector3 s1 = selectedStations[0].ms60.position;
            Vector3 s2 = selectedStations[1].ms60.position;
            double dx = s2.x - s1.x, dy = s2.y - s1.y, dz = s2.z - s1.z;
            double dist_sq = dx * dx + dy * dy + dz * dz;
            if (dist_sq > 1e-12)
            {
                double w_scale = 1e15 / dist_sq;   // было 1e12
                for (int i = 0; i < 6; i++)
                    for (int j = 0; j < 6; j++)
                    {
                        double val = (i < 3 ? (j < 3 ? 1 : -1) : (j < 3 ? -1 : 1)) *
                                     new[] { dx, dy, dz, dx, dy, dz }[i] * new[] { dx, dy, dz, dx, dy, dz }[j];
                        N[i, j] += w_scale * val;
                    }
            }
        }
        if (nStations >= 3) { N[8, 8] += 1e15; u[8] += 1e15 * 0; }

        const double reg = 1e-10;
        for (int i = 0; i < n; i++) { N[i, i] += reg; }

        if (!JacobiEigenDecomposition(N, out double[] ev, out _))
        {
            isDegenerate = true;
            return new NormalMatrixResult(N, u);
        }
        isDegenerate = (ev.Min() < reg * 0.1);

        return new NormalMatrixResult(N, u);
    }

    private void AccumulateRowToNAndU(double[,] N, double[] u, double[] row, double misclosure, double weight)
    {
        int n = N.GetLength(0);

        // N += weight * rowᵀ * row
        for (int i = 0; i < n; i++)
        {
            double ri = row[i];
            if (Math.Abs(ri) < 1e-16) continue;

            // u += weight * row * misclosure
            u[i] += weight * ri * misclosure;

            for (int j = 0; j < n; j++)
            {
                double rj = row[j];
                if (Math.Abs(rj) < 1e-16) continue;
                N[i, j] += weight * ri * rj;
            }
        }
    }


    private double[] SolveGauss(double[,] A, double[] b)
    {
        int n = A.GetLength(0);
        if (n == 0) return null;

        double[,] M = new double[n, n + 1];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++) M[i, j] = A[i, j];
            M[i, n] = b[i];
        }

        for (int col = 0; col < n; col++)
        {
            int mx = col;
            for (int r = col + 1; r < n; r++)
                if (Math.Abs(M[r, col]) > Math.Abs(M[mx, col])) mx = r;

            if (Math.Abs(M[mx, col]) < 1e-15) return null;
            if (mx != col)
                for (int j = col; j <= n; j++)
                {
                    var t = M[col, j]; M[col, j] = M[mx, j]; M[mx, j] = t;
                }

            for (int r = col + 1; r < n; r++)
            {
                double f = M[r, col] / M[col, col];
                for (int j = col; j <= n; j++) M[r, j] -= f * M[col, j];
            }
        }

        double[] x = new double[n];
        for (int i = n - 1; i >= 0; i--)
        {
            double s = M[i, n];
            for (int j = i + 1; j < n; j++) s -= M[i, j] * x[j];
            x[i] = s / M[i, i];
        }
        return x;
    }
    private bool SolveWithSVD(double[,] A, double[] b, out double[] x, double tol = 1e-12)
    {
        x = null;
        int n = A.GetLength(0);
        if (!JacobiEigenDecomposition(A, out double[] eig, out double[,] V))
            return false;

        double maxEig = eig.Max();
        double threshold = tol * maxEig;

        x = new double[n];
        for (int k = 0; k < n; k++)
        {
            if (eig[k] < threshold) continue;
            double inv = 1.0 / eig[k];
            double dot = 0;
            for (int i = 0; i < n; i++)
                dot += V[i, k] * b[i];
            double coeff = dot * inv;
            for (int i = 0; i < n; i++)
                x[i] += coeff * V[i, k];
        }
        return true;
    }
    // Возвращает список рекомендованных позиций для добавления станции с оценкой cond (меньше лучше)
    private List<(Vector3 pos, double cond)> SuggestStationPositions(int topK = 5)
    {
        var suggestions = new List<(Vector3 pos, double cond)>();

        if (targetBuilding == null || grades.Count == 0)
        {
            Debug.LogWarning("Не задано здание или нет марок");
            return suggestions;
        }

        Bounds bounds = GetObjectBounds(targetBuilding);
        Collider buildingCollider = targetBuilding.GetComponent<Collider>();

        if (buildingCollider == null)
        {
            Debug.LogWarning("Нет коллайдера у здания");
            return suggestions;
        }

        // Генерируем точки не только по периметру, но и с учетом геометрии
        var candidatePositions = GenerateGeometricCandidatePositions(bounds, buildingCollider);

        if (candidatePositions == null || candidatePositions.Count == 0)
            return suggestions;

        // Сохраняем исходные станции
        var originalStations = new List<Station>(selectedStations);

        foreach (var candidatePos in candidatePositions)
        {
            // Проверяем базовые условия
            if (IsInsideAnyBuilding(candidatePos))
                continue;

            // Проверяем расстояние до существующих станций
            bool tooClose = selectedStations.Any(s => Vector3.Distance(s.position, candidatePos) < 5f);
            if (tooClose)
                continue;

            // Проверяем видимость марок
            var visibleGrades = GetVisibleGradesFromPos(candidatePos);
            if (visibleGrades.Count < 3) // Новая станция должна видеть минимум 3 марки
                continue;

            // Создаем временную станцию
            var tempStation = new Station
            {
                position = candidatePos,
                visibleGrades = visibleGrades,
                ms60 = null // Не нужно для расчета матрицы
            };

            // Добавляем временную станцию
            selectedStations.Add(tempStation);

            try
            {
                // Вычисляем число обусловленности
                double cond;
                double[,] covariance;
                bool ok = ComputeConditionNumberAndInverse(out cond, out covariance);

                if (ok && !double.IsInfinity(cond) && !double.IsNaN(cond))
                {
                    // Учитываем не только cond, но и улучшение покрытия
                    double improvementScore = CalculateCoverageImprovement(visibleGrades);
                    double weightedCond = cond / (1.0 + improvementScore * 0.1);

                    suggestions.Add((candidatePos, weightedCond));
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Ошибка расчета для кандидата {candidatePos}: {ex.Message}");
            }

            // Удаляем временную станцию
            selectedStations.RemoveAt(selectedStations.Count - 1);
        }

        // Восстанавливаем исходные станции
        selectedStations.Clear();
        selectedStations.AddRange(originalStations);

        // Сортируем по улучшению cond (чем меньше, тем лучше)
        var sortedSuggestions = suggestions
            .OrderBy(s => s.cond)
            .Take(topK)
            .ToList();

        Debug.Log($"Найдено {sortedSuggestions.Count} рекомендуемых позиций из {candidatePositions.Count} кандидатов");

        return sortedSuggestions;
    }

    private List<Vector3> GenerateGeometricCandidatePositions(Bounds bounds, Collider buildingCollider)
    {
        var candidates = new List<Vector3>();

        // 1. Точки на оптимальном расстоянии от центра
        float optimalDistance = CalculateOptimalDistance(bounds);

        // 2. Генерация в секторах с учетом непокрытых марок
        var uncoveredGrades = GetUncoveredGrades();
        int sectors = Mathf.Max(8, Mathf.Min(uncoveredGrades.Count * 2, 24));

        for (int i = 0; i < sectors; i++)
        {
            float angle = i * (360f / sectors) * Mathf.Deg2Rad;

            // Разные расстояния для разнообразия
            for (int d = 0; d < 3; d++)
            {
                float distance = optimalDistance * (0.7f + d * 0.3f);
                Vector3 direction = new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle));
                Vector3 candidate = bounds.center + direction * distance;

                // Проверка на земле и не в здании
                if (Physics.Raycast(candidate + Vector3.up * 50f, Vector3.down, out RaycastHit hit, 60f, groundLayer))
                {
                    Vector3 groundPos = hit.point;
                    if (!IsInsideAnyBuilding(groundPos))
                    {
                        candidates.Add(groundPos);
                    }
                }
            }
        }

        // 3. Точки напротив непокрытых марок
        foreach (var grade in uncoveredGrades)
        {
            Vector3 toGrade = (grade.position - bounds.center).normalized;

            for (int i = -1; i <= 1; i++)
            {
                float angleOffset = i * 30f;
                Vector3 direction = Quaternion.Euler(0, angleOffset, 0) * toGrade;
                Vector3 candidate = grade.position + direction * optimalDistance;

                if (Physics.Raycast(candidate + Vector3.up * 50f, Vector3.down, out RaycastHit hit, 60f, groundLayer))
                {
                    Vector3 groundPos = hit.point;
                    if (!IsInsideAnyBuilding(groundPos))
                    {
                        candidates.Add(groundPos);
                    }
                }
            }
        }

        return candidates.Distinct().ToList();
    }

    private HashSet<Transform> GetUncoveredGrades()
    {
        var covered = new HashSet<Transform>();
        foreach (var station in selectedStations)
        {
            if (station.visibleGrades != null)
                covered.UnionWith(station.visibleGrades);
        }

        var uncovered = new HashSet<Transform>(grades.Except(covered));
        return uncovered;
    }

    private double CalculateCoverageImprovement(HashSet<Transform> newVisibleGrades)
    {
        var currentlyCovered = new HashSet<Transform>();
        foreach (var station in selectedStations)
        {
            if (station.visibleGrades != null)
                currentlyCovered.UnionWith(station.visibleGrades);
        }

        // Новые марки, которые покроет дополнительная станция
        var newCoverage = new HashSet<Transform>(newVisibleGrades.Except(currentlyCovered));
        return newCoverage.Count;
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
            // Рассчитываем «апостериори» как минимальную дистанцию до видимых станций
            var visibleStations = selectedStations
                .Where(s => s.visibleGrades.Contains(g))
                .ToList();

            if (visibleStations.Count == 0)
            {
                Debug.Log($"Марка {g.name}: недоступна для измерения (не видна ни одной станцией).");
                continue;
            }

            // Простая оценка σX и σY через расстояние до ближайшей станции
            Station nearest = visibleStations.OrderBy(s => Vector3.Distance(s.ms60.position, g.position)).First();
            float dist = Vector3.Distance(nearest.ms60.position, g.position);

            // Примерная апостериори (можно масштабировать по требованиям вашей методики)
            float sx = dist * 0.001f; // в метрах
            float sy = dist * 0.001f;
            float sp = Mathf.Sqrt(sx * sx + sy * sy);

            Debug.Log($"Марка {g.name}: σX={sx:F4}, σY={sy:F4}, σp={sp:F4}");
        }
    }
    private void ExportStationsToCSV()
    {
        if (selectedStations == null || selectedStations.Count == 0)
        {
            Debug.LogWarning("Нет станций для экспорта");
            return;
        }

        string buildingName = buildingDropdown.options[buildingDropdown.value].text;
        GameObject building = GameObject.Find(buildingName);

        if (building == null)
        {
            Debug.LogError($"Здание '{buildingName}' не найдено");
            return;
        }

        var meshColliders = building
            .GetComponentsInChildren<MeshCollider>(true)
            .Where(c => c.sharedMesh != null)
            .ToList();

        if (meshColliders.Count == 0)
        {
            Debug.LogError("В здании отсутствуют MeshCollider");
            return;
        }

        string path = Path.Combine(Application.streamingAssetsPath, "station_route.txt");
        Directory.CreateDirectory(Application.streamingAssetsPath);

        try
        {
            using (var writer = new StreamWriter(path, false, Encoding.UTF8))
            {
                // ===== ЗДАНИЕ =====
                writer.WriteLine($"BUILDING;{building.name}");
                writer.WriteLine();

                // ===== ФАСАДЫ =====
                writer.WriteLine("FACADES;COLLIDER;NX;NY;NZ;PX;PY;PZ");

                foreach (var collider in meshColliders)
                    ExtractAndExportFacades(collider, writer);

                writer.WriteLine();

                // ===== МАРКИ =====
                writer.WriteLine("GRADES;X;Y;Z");
                foreach (Transform t in building.GetComponentsInChildren<Transform>(true))
                {
                    if (!t.CompareTag("grade")) continue;

                    Vector3 p = t.position;
                    writer.WriteLine($"{t.name};{p.x:F3};{p.y:F3};{p.z:F3}");
                }

                writer.WriteLine();

                // ===== СТАНЦИИ =====
                writer.WriteLine("STATIONS;X;Y;Z");
                for (int i = 0; i < selectedStations.Count; i++)
                {
                    Vector3 pos = selectedStations[i].position;
                    writer.WriteLine($"Station{i + 1};{pos.x:F3};{pos.y:F3};{pos.z:F3}");
                }
            }

            Debug.Log($"Экспорт завершён: {path}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"Ошибка экспорта: {ex.Message}");
        }
    }
    private void ExtractAndExportFacades(MeshCollider collider, StreamWriter writer)
    {
        Mesh mesh = collider.sharedMesh;
        Vector3[] v = mesh.vertices;
        int[] t = mesh.triangles;
        Transform tr = collider.transform;

        Dictionary<Vector3, List<Vector3>> clusters = new();

        for (int i = 0; i < t.Length; i += 3)
        {
            Vector3 p0 = tr.TransformPoint(v[t[i]]);
            Vector3 p1 = tr.TransformPoint(v[t[i + 1]]);
            Vector3 p2 = tr.TransformPoint(v[t[i + 2]]);

            Vector3 normal = Vector3.Cross(p1 - p0, p2 - p0);
            float area = normal.magnitude * 0.5f;

            if (area < 0.05f) continue; // фильтр шума

            normal.Normalize();

            // игнор крыши и пола
            if (Mathf.Abs(normal.y) > 0.3f) continue;

            Vector3 qNormal = QuantizeNormal(normal, 0.1f);

            if (!clusters.ContainsKey(qNormal))
                clusters[qNormal] = new List<Vector3>();

            clusters[qNormal].Add((p0 + p1 + p2) / 3f);
        }

        int id = 1;
        foreach (var kv in clusters)
        {
            Vector3 center =
                kv.Value.Aggregate(Vector3.zero, (a, b) => a + b) / kv.Value.Count;

            Vector3 n = kv.Key;

            writer.WriteLine(
                $"FACADE;{collider.name}_F{id};" +
                $"{n.x:F4};{n.y:F4};{n.z:F4};" +
                $"{center.x:F3};{center.y:F3};{center.z:F3}"
            );

            id++;
        }
    }
    private Vector3 QuantizeNormal(Vector3 n, float step)
    {
        return new Vector3(
            Mathf.Round(n.x / step) * step,
            0f,
            Mathf.Round(n.z / step) * step
        ).normalized;
    }
    public void ClearStations()
    {
        // удаляет ссылку на выбранные станции; фактические префабы остаются в parentContainer
        selectedStations.Clear();
        Debug.Log("selectedStations очищен.");
    }
}
