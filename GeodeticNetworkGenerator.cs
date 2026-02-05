using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using TMPro;
using Unity.VisualScripting;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using Debug = UnityEngine.Debug;
using System.Text.RegularExpressions;
using MathNet;



public class GeodeticNetworkGenerator : MonoBehaviour
{
    [Header("Настройки")]
    public GameObject networkPrefab;
    public TMP_Dropdown buildingDropdown;
    public float offset = 5f;
    public Transform parentContainer;
    [Header("Raycast")]
    public float raycastHeight = 5f;
    public LayerMask groundLayer;
    public LayerMask obstacleLayer;
    [Header("Ограничения количества станций")]
    public int minStations = 3;
    [Header("py файл (указываем через Inspector)")]
    [Header("СКО")]
    public TMP_InputField directionStdevInput;
    [Header("UI")]
    public TMP_Text resultText;
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
        ExportXML(); // 
        RunPython(); // 
        PrintNetworkReport();
        ExportStationsToCSV();

    }


    // ЗАМЕНИТЕ существующий метод GenerateNetwork на этот:
    private void GenerateNetwork()
    {
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

    private bool IsResectionClosed(List<Station> solution, Bounds bounds, Collider buildingCollider)
    {
        if (solution == null || solution.Count < minStations) return false;

        // 1. Быстрая проверка: все станции должны видеть хотя бы одну марку
        foreach (var s in solution)
        {
            if (s.visibleGrades == null || s.visibleGrades.Count == 0)
                return false;

            if (IsInsideAnyBuilding(s.position))
                return false;
        }

        // 2. Упрощённая проверка покрытия: хотя бы 80% марок должны быть видны
        var covered = new HashSet<Transform>();
        foreach (var s in solution)
        {
            foreach (var grade in s.visibleGrades)
            {
                covered.Add(grade);
            }
        }

        // УСПОКОИЛИ условие: достаточно 80% покрытия
        float coverageRatio = (float)covered.Count / grades.Count;
        return coverageRatio >= 0.8f; // Было: coverageRatio == 1.0f
    }
    private Dictionary<Vector3Int, HashSet<Transform>> visibilityCache = new Dictionary<Vector3Int, HashSet<Transform>>();
    private void ClearCache()
    {
        if (visibilityCache != null)
            visibilityCache.Clear();
    }

    private static void ShuffleInPlace<T>(IList<T> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = UnityEngine.Random.Range(0, i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
    private HashSet<Transform> GetCachedVisibleGrades(Vector3 pos)
    {
        // Округляем позицию для ключа кэша
        Vector3Int key = new Vector3Int(
            Mathf.RoundToInt(pos.x),
            Mathf.RoundToInt(pos.y),
            Mathf.RoundToInt(pos.z)
        );

        if (visibilityCache.TryGetValue(key, out var cached))
            return cached;

        var visible = GetVisibleGradesFromPos(pos);
        visibilityCache[key] = visible;
        return visible;
    }

    // ДОБАВЬТЕ эти методы для генетического алгоритма:

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

        int populationSize = Mathf.Clamp(grades.Count * 2, 10, 30); // Увеличили популяцию
        int generations = Mathf.Clamp(grades.Count * 3, 30, 60);    // Увеличили поколения
        float mutationRate = 0.6f;  // ВЫСОКИЙ шанс мутации для разнообразия
        float crossoverRate = 0.8f; // ВЫСОКИЙ шанс кроссовера
        int eliteCount = Mathf.Clamp(populationSize / 5, 3, 8); // Элитизм

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
        if (candidatePositions.Count > 80)
        {
            ShuffleInPlace(candidatePositions);
            candidatePositions = candidatePositions
                .Take(80)
                .ToList();
            Debug.Log($"Ограничено количество кандидатов до {candidatePositions.Count}");
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

                if (coveredGrades.Count == grades.Count)
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
                            Debug.Log($"✅ Поколение {generation}: Полное покрытие! Фитнес={bestFitness:F0}, " +
                                     $"Станций={bestSolution.Count}");
                        }

                        // РАННИЙ ВЫХОД если нашли хорошее компактное решение
                        if (bestSolution.Count <= 8 && fitness > 1000000f)
                        {
                            Debug.Log($"🎯 Ранний выход: найдено оптимальное решение на поколении {generation}");
                            stopwatch.Stop();
                            return bestSolution;
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

                // 50% вероятности использовать кроссовер, 50% - клонирование
                List<Station> offspring;
                if (UnityEngine.Random.value < 0.5f && fitnessScores.Count >= 2)
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

                var shuffled = new List<Vector3>(candidatePositions);
                ShuffleInPlace(shuffled);
                for (int i = 0; i < stationCount && i < shuffled.Count; i++)
                {
                    var visible = GetCachedVisibleGrades(shuffled[i]);
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

                    return fallbackFitness > bestFitnessVal ? fallback : bestSolution;
                }
            }

            return bestSolution.Where(s => s != null).ToList();
        }
        else
        {
            Debug.LogWarning("ГА не нашел решения, используем fallback");
            return GenerateFallbackSolution(bounds, buildingCollider, baseOffset);
        }
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
        int restCount = populationSize - greedyCount - randomCount - geometricCount - uniformCount; // Остальные - смешанные

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

        // ================== 6. ДОПОЛНЕНИЕ СМЕШАННЫМИ РЕШЕНИЯМИ ==================
        while (population.Count < populationSize)
        {
            int strategy = UnityEngine.Random.Range(0, 4);
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
            ShuffleInPlace(availableCandidates);

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

        var shuffled = new List<Vector3>(candidatePositions);
        ShuffleInPlace(shuffled);
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
        var shuffled = new List<Vector3>(candidatePositions);
        ShuffleInPlace(shuffled);

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
    private List<Vector3> GenerateCandidatePositions(Bounds bounds, Collider buildingCollider, float offset)
    {
        var candidates = new List<Vector3>();

        // ОГРАНИЧИВАЕМ количество точек при большом количестве марок
        int pointsPerSide = grades.Count > 15 ? 15 : 30;
        float rayStartOffset = 30f; // Уменьшено

        // УПРОЩАЕМ генерацию: только основные направления
        for (int i = 0; i < 360; i += 360 / pointsPerSide)
        {
            float angle = i * Mathf.Deg2Rad;

            // Генерируем только на оптимальном расстоянии, не несколько вариантов
            Vector3 dir = new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle));
            Vector3 point = bounds.center + dir * offset;

            Vector3 rayStart = point + Vector3.up * rayStartOffset;

            if (Physics.Raycast(rayStart, Vector3.down, out RaycastHit hit,
                rayStartOffset + 20f, groundLayer)) // Уменьшен диапазон
            {
                Vector3 groundPos = hit.point;

                // Быстрая проверка на нахождение в здании
                if (!IsInsideAnyBuilding(groundPos))
                {
                    // Проверяем минимальное расстояние до стены
                    Vector3 wallPoint = buildingCollider.ClosestPoint(groundPos);
                    if (Vector3.Distance(groundPos, wallPoint) > 3.0f) // Уменьшен минимум
                    {
                        candidates.Add(groundPos);
                    }
                }
            }
        }

        // Дополнительные точки вокруг КАЖДОЙ марки - только если марок не слишком много
        if (grades.Count <= 20)
        {
            foreach (var grade in grades)
            {
                Vector3 toGrade = (grade.position - bounds.center).normalized;

                // Только 3 угла вместо 7
                float[] angleOffsets = { -30f, 0f, 30f };

                foreach (float angleOffset in angleOffsets)
                {
                    Vector3 dir = Quaternion.Euler(0, angleOffset, 0) * toGrade;

                    // Только 2 расстояния вместо 4
                    for (int d = 0; d < 2; d++)
                    {
                        float distance = offset * (0.8f + d * 0.4f);
                        Vector3 point = grade.position + dir * distance;
                        Vector3 rayStart = point + Vector3.up * rayStartOffset;

                        if (Physics.Raycast(rayStart, Vector3.down, out RaycastHit hit,
                            rayStartOffset + 20f, groundLayer))
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

        // Удаляем дубликаты с большим порогом для уменьшения количества
        var uniqueCandidates = new List<Vector3>();
        foreach (var candidate in candidates)
        {
            bool isDuplicate = false;
            foreach (var unique in uniqueCandidates)
            {
                if (Vector3.Distance(candidate, unique) < 5f) // Увеличен порог
                {
                    isDuplicate = true;
                    break;
                }
            }
            if (!isDuplicate)
                uniqueCandidates.Add(candidate);
        }

        Debug.Log($"Сгенерировано {uniqueCandidates.Count} валидных позиций (оптимизировано)");
        return uniqueCandidates;
    }

    private float CalculateOptimalDistance(Bounds bounds)
    {
        // УМЕНЬШАЕМ расстояния для более близкого размещения
        float buildingSize = bounds.size.magnitude;
        float minDistance = Mathf.Max(buildingSize * 0.1f, 6f);  // 
        float maxDistance = Mathf.Max(buildingSize * 0.3f, 12f); // 

        Debug.Log($"Размер здания: {buildingSize:F1}m, оптимальное расстояние: {minDistance:F1}-{maxDistance:F1}m");
        return (minDistance + maxDistance) / 2f;
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

        // ================== 6. МИНИМИЗАЦИЯ КОЛИЧЕСТВА СТАНЦИЙ ==================
        int optimalCount = Mathf.Max(minStations, Mathf.CeilToInt(grades.Count / 2.0f));

        if (solution.Count > optimalCount)
        {
            int excessStations = solution.Count - optimalCount;
            fitness -= excessStations * 30000f;
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

                    if (angle >= 60f && angle <= 120f)
                    {
                        hasGoodAngle = true;
                        fitness += 2000f;
                    }
                    else if (angle < 30f || angle > 150f)
                    {
                        fitness -= 5000f;
                    }
                }
            }

            totalMinAngle += minAngle;
            if (hasGoodAngle) marksWithGoodAngles++;
        }

        if (grades.Count > 0)
        {
            float avgMinAngle = totalMinAngle / grades.Count;
            if (avgMinAngle >= 50f) fitness += 10000f;
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

        // ================== 10. ФИНАЛЬНЫЙ ШТРАФ/БОНУС ==================
        fitness -= solution.Count * 3000f;
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
        Debug.LogWarning("Используется усиленное резервное решение");
        var solution = new List<Station>();
        var uncoveredGrades = new HashSet<Transform>(grades);
        var candidatePositions = GenerateCandidatePositions(bounds, buildingCollider, baseOffset);

        // Жадный алгоритм: добавляем станции пока не покроем все марки
        while (uncoveredGrades.Count > 0 && candidatePositions.Count > 0)
        {
            // Находим позицию, которая покрывает больше всего непокрытых марок
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
                Debug.Log($"Fallback: добавлена станция, покрыто {bestCoveredCount} новых марок");
            }

            candidatePositions.Remove(bestPos);

            // Защита от бесконечного цикла
            if (solution.Count > 15) break;
        }

        if (uncoveredGrades.Count == 0)
        {
            Debug.Log("✅ Fallback решение покрыло ВСЕ марки!");
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

    // Проверка нахождения внутри здания
    private bool IsInsideBuildingPhysics(Vector3 pos, Collider buildingCollider)
    {
        if (buildingCollider == null) return false;

        // 1. Проверка через bounds - быстрая предварительная проверка
        if (!buildingCollider.bounds.Contains(pos))
            return false;

        // 2. Проверка через OverlapSphere с увеличенным радиусом
        float checkRadius = 1.0f; // Увеличиваем радиус проверки
        Collider[] hits = Physics.OverlapSphere(pos, checkRadius);
        foreach (var hit in hits)
        {
            if (hit == buildingCollider || hit.CompareTag("building"))
            {
                Debug.Log($"Обнаружено здание в радиусе {checkRadius}m от точки {pos}");
                return true;
            }
        }

        // 3. Проверка через ClosestPoint
        Vector3 closestPoint = buildingCollider.ClosestPoint(pos);
        float distanceToClosest = Vector3.Distance(pos, closestPoint);
        if (distanceToClosest < 0.1f) // Увеличиваем порог
        {
            Debug.Log($"Точка слишком близко к зданию! Расстояние: {distanceToClosest:F3}m");
            return true;
        }

        // 4. Дополнительная проверка: Raycast в нескольких направлениях
        int buildingHits = 0;
        Vector3[] rayDirections = {
        Vector3.forward, Vector3.back, Vector3.left, Vector3.right,
        new Vector3(1, 0, 1).normalized, new Vector3(-1, 0, 1).normalized,
        new Vector3(1, 0, -1).normalized, new Vector3(-1, 0, -1).normalized
    };

        foreach (Vector3 dir in rayDirections)
        {
            if (Physics.Raycast(pos, dir, out RaycastHit hit, 5f))
            {
                if (hit.collider == buildingCollider || hit.collider.CompareTag("building"))
                {
                    buildingHits++;
                }
            }
        }

        // Если с большинства направлений попадаем в здание - вероятно внутри
        if (buildingHits >= 6)
        {
            Debug.Log($"Точка внутри здания (попадания с {buildingHits} направлений)");
            return true;
        }

        return false;
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
        // Параметры
        float maxDistance = 100f;
        float rayRadius = 0.05f; // 5 см — реалистичный радиус лазерного луча

        Vector3 dir = toPos - fromPos;
        float dist = dir.magnitude;

        if (dist > maxDistance) return false;
        if (dist <= 0.001f) return true;

        // Поднимаем луч чуть выше земли, чтобы избежать ложных срабатываний
        Vector3 fromAdjusted = fromPos + Vector3.up * 0.1f;
        Vector3 toAdjusted = toPos + Vector3.up * 0.1f;
        dir = toAdjusted - fromAdjusted;
        dist = dir.magnitude;

        // Используем SphereCast вместо Raycast — учитывает толщину луча
        if (Physics.SphereCast(
            fromAdjusted,
            rayRadius,
            dir.normalized,
            out RaycastHit hit,
            dist,
            obstacleLayer,
            QueryTriggerInteraction.Collide))
        {
            // Если попали не в конечную точку — есть препятствие
            if (Vector3.Distance(hit.point, toAdjusted) > 0.1f)
                return false;
        }

        return true;
    }

    // Дополнительные вспомогательные методы для проверки принадлежности к зданию
    private bool IsChildOfBuilding(Transform obj)
    {
        Transform current = obj;
        while (current.parent != null)
        {
            if (current.parent.CompareTag("building"))
                return true;
            current = current.parent;
        }
        return false;
    }

    private bool IsPartOfBuildingStructure(Transform obj)
    {
        // Проверяем по имени (часто части зданий имеют специфичные имена)
        string lowerName = obj.name.ToLower();
        if (lowerName.Contains("wall") || lowerName.Contains("roof") ||
            lowerName.Contains("floor") || lowerName.Contains("column") ||
            lowerName.Contains("beam") || lowerName.Contains("window") ||
            lowerName.Contains("door") || lowerName.Contains("facade"))
        {
            return true;
        }

        // Проверяем родителя
        if (obj.parent != null)
        {
            string parentLowerName = obj.parent.name.ToLower();
            if (parentLowerName.Contains("building") || parentLowerName.Contains("house") ||
                parentLowerName.Contains("structure"))
            {
                return true;
            }
        }

        return false;
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

            // Добавление случайного шума (в метрах)
            float noiseX = UnityEngine.Random.Range(-coordNoise, coordNoise) / 1000f;
            float noiseZ = UnityEngine.Random.Range(-coordNoise, coordNoise) / 1000f;

            float noisyX = relativePos.x + noiseX;
            float noisyZ = relativePos.z + noiseZ;

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
                    float dist = Vector3.Distance(fromPos, toPos);

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
    private void ShowGradeShiftSuggestions(double[,] covariance, double targetSigma = 0.003)
    {
        if (covariance == null) return;

        int M = grades.Count;

        Debug.Log("=== РЕКОМЕНДАЦИИ ПО УЛУЧШЕНИЮ ГЕОМЕТРИИ МАРОК ===");

        for (int gi = 0; gi < M; gi++)
        {
            int b = gi * 3;
            if (b + 2 >= covariance.GetLength(0)) continue;

            // Ковариация марки (3×3)
            double[,] C = new double[3, 3];
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    C[i, j] = covariance[b + i, b + j];

            // Собственные значения и векторы
            if (!JacobiEigenDecomposition(C, out double[] eig, out double[,] vec))
                continue;

            // Наибольшее собственное значение → худшее направление
            int kMax = 0;
            for (int k = 1; k < 3; k++)
                if (eig[k] > eig[kMax]) kMax = k;

            double sigmaWorst = Math.Sqrt(eig[kMax]);
            if (sigmaWorst <= targetSigma)
                continue; // марка уже хорошая

            // Направление ухудшения
            Vector3 dir = new Vector3(
                (float)vec[0, kMax],
                (float)vec[1, kMax],
                (float)vec[2, kMax]
            ).normalized;

            // Сколько нужно уменьшить дисперсию
            double factor = (sigmaWorst - targetSigma) / sigmaWorst;
            double shiftMeters = Math.Clamp(factor * 0.3, 0.05, 0.5); // 5–50 см

            // Интерпретация направления
            string directionText =
                Math.Abs(dir.x) > Math.Abs(dir.z)
                    ? (dir.x > 0 ? "вправо" : "влево")
                    : (dir.z > 0 ? "вперёд" : "назад");

            Debug.Log(
                $"Марка {grades[gi].name}: " +
                $"σ={sigmaWorst * 1000:F1} мм → " +
                $"рекомендуется сместить {directionText} " +
                $"примерно на {shiftMeters * 100:F0} см"
            );
        }
    }


    // ==========================================================================
    //                           NETWORK REPORT
    // ==========================================================================

    public void PrintNetworkReport()
    {
        if (grades == null || grades.Count == 0)
        {
            Debug.LogWarning("Нет марок — отчёт невозможен.");
            return;
        }

        if (selectedStations.Count < minStations)
        {
            Debug.LogWarning($"Недостаточно станций: {selectedStations.Count} < {minStations}");
            return;
        }

        // ===== 1. СЫРАЯ НОРМАЛЬНАЯ МАТРИЦА (чистая геометрия сети) =====
        double[,] N_raw = BuildRawNormalMatrix();
        if (N_raw == null || N_raw.GetLength(0) == 0)
        {
            Debug.LogError("Не удалось построить нормальную матрицу.");
            return;
        }

        // ===== 2. СПЕКТРАЛЬНЫЙ АНАЛИЗ СЫРОЙ МАТРИЦЫ =====
        if (!JacobiEigenDecomposition(N_raw, out double[] eig_raw, out _))
        {
            Debug.LogError("Не удалось выполнить спектральный анализ сырой матрицы.");
            return;
        }

        Array.Sort(eig_raw); // сортируем по возрастанию
        double threshold = eig_raw[eig_raw.Length - 1] * 1e-12;
        int zeroCount = 0;
        for (int i = 0; i < Math.Min(7, eig_raw.Length); i++)
            if (eig_raw[i] < threshold) zeroCount++;

        Debug.Log("=== АНАЛИЗ СПЕКТРА СОБСТВЕННЫХ ЗНАЧЕНИЙ ===");
        Debug.Log($"Неопределённых степеней свободы: {zeroCount}/7");
        if (zeroCount > 0)
            Debug.LogWarning($"СЕТЬ ИМЕЕТ {zeroCount} НЕОПРЕДЕЛЁННЫХ СТЕПЕНЕЙ СВОБОДЫ!");

        // ===== 3. ЧИСЛО ОБУСЛОВЛЕННОСТИ ГЕОМЕТРИИ =====
        // Пропускаем первые 6 собственных значений (3 сдвига + 3 поворота)
        // Масштаб фиксирован измерениями расстояний
        int startIdx = 6;
        if (eig_raw.Length <= startIdx)
        {
            Debug.LogError("Недостаточно измерений для оценки геометрии сети.");
            return;
        }

        double lambda_min = eig_raw[startIdx];
        double lambda_max = eig_raw[eig_raw.Length - 1];
        double cond_geometric = (lambda_min < threshold)
            ? double.PositiveInfinity
            : lambda_max / lambda_min;

        // ===== 4. ЗАФИКСИРОВАННАЯ МАТРИЦА ДЛЯ КОВАРИАЦИОННОЙ МАТРИЦЫ =====
        double[,] N_fixed = ComputeNormalMatrix(out bool isDegenerate);
        if (N_fixed == null || isDegenerate)
        {
            Debug.LogError("Не удалось построить зафиксированную нормальную матрицу.");
            return;
        }

        if (!JacobiEigenDecomposition(N_fixed, out double[] eig_fixed, out double[,] vec_fixed))
        {
            Debug.LogError("Не удалось инвертировать зафиксированную матрицу.");
            return;
        }

        int n = N_fixed.GetLength(0);
        double[,] invN = new double[n, n];
        double regularization = 1e-10;

        for (int k = 0; k < eig_fixed.Length; k++)
        {
            double lambda = Math.Max(eig_fixed[k], regularization);
            double lambdaInv = 1.0 / lambda;

            for (int i = 0; i < n; i++)
            {
                double vik = vec_fixed[i, k];
                if (Math.Abs(vik) < 1e-15) continue;
                for (int j = 0; j < n; j++)
                {
                    double vjk = vec_fixed[j, k];
                    if (Math.Abs(vjk) < 1e-15) continue;
                    invN[i, j] += vik * lambdaInv * vjk;
                }
            }
        }

        // ===== 5. ОТЧЁТ =====
        Debug.Log("\n=== ОТЧЁТ О ГЕОМЕТРИИ И ТОЧНОСТИ СЕТИ ===");
        Debug.Log($"Марок: {grades.Count}");
        Debug.Log($"Станций: {selectedStations.Count}");
        Debug.Log($"Число обусловленности (геометрия сети): {cond_geometric:N0}");

        // Интерпретация числа обусловленности
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

        // ===== 6. ОЦЕНКА ТОЧНОСТИ МАРОК (σ₀ = 1 — относительная оценка) =====
        Debug.Log("\n=== ОЦЕНКА ОТНОСИТЕЛЬНОЙ ТОЧНОСТИ (σ₀ = 1) ===");
        Debug.Log("Внимание: для абсолютных ошибок требуется апостериорная дисперсия из невязок!");

        double maxSigma = 0, sumSigma = 0;
        int count = 0;
        int offset = 3 * selectedStations.Count; // марки начинаются после станций

        for (int i = 0; i < grades.Count; i++)
        {
            int k = offset + i * 3;
            if (k + 2 >= n) continue;

            // Извлечение дисперсий (диагональные элементы ковариационной матрицы)
            double sx = Math.Sqrt(Math.Max(0, invN[k, k]));
            double sy = Math.Sqrt(Math.Max(0, invN[k + 1, k + 1]));
            double sz = Math.Sqrt(Math.Max(0, invN[k + 2, k + 2]));

            // Система координат Unity: X,Z — план, Y — высота
            double sigmaPlan = Math.Sqrt(sx * sx + sz * sz); // план = X + Z
            double sigma3D = Math.Sqrt(sx * sx + sy * sy + sz * sz);

            maxSigma = Math.Max(maxSigma, sigma3D);
            sumSigma += sigma3D;
            count++;

            Debug.Log(
                $"Марка {grades[i].name}: " +
                $"σX={sx * 1000:F1}мм, σY={sy * 1000:F1}мм, σZ={sz * 1000:F1}мм, " +
                $"σплан={sigmaPlan * 1000:F1}мм, σ3D={sigma3D * 1000:F1}мм"
            );
        }

        if (count > 0)
        {
            Debug.Log($"\nСредняя σ3D: {(sumSigma / count) * 1000:F1} мм");
            Debug.Log($"Максимальная σ3D: {maxSigma * 1000:F1} мм");
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

        Debug.Log("\n=== ОТЧЁТ ЗАВЕРШЁН ===");
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
        }

        if (nStations >= 3)
        {
            // Условие 6: устранение поворота вокруг оси между ст.1 и ст.2
            // Так как ст.1=(0,0,0), ст.2=(X₂,0,0), то плоскость ст.1-ст.2 = XY
            // Фиксируем Z-координату третьей станции: Z₃ = 0
            N[8, 8] += 1e12; // Z₃ = 0 ← КРИТИЧЕСКОЕ ИСПРАВЛЕНИЕ!
        }

        // Условие 7: масштаб фиксирован измерениями расстояний
        // (если расстояний нет, добавьте слабое условие на базисное расстояние)

        // ===== ЧИСЛЕННАЯ СТАБИЛИЗАЦИЯ (только для решения СЛАУ) =====
        const double regularization = 1e-10;
        for (int i = 0; i < n; i++)
            N[i, i] += regularization;

        // ===== ПРОВЕРКА ВЫРОЖДЕННОСТИ =====
        // После полной фиксации матрица должна быть невырожденной
        // Проверяем только численную стабильность
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

        return N; // ← ЧИСТАЯ ГЕОМЕТРИЯ СЕТИ, без искажений!
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
            Debug.LogWarning("Нет станций для экспорта в TXT");
            return;
        }

        string txtPath = Path.Combine(Application.streamingAssetsPath, "station_route.txt");
        Directory.CreateDirectory(Application.streamingAssetsPath);

        try
        {
            using (var writer = new StreamWriter(txtPath, false, Encoding.UTF8))
            {
                // Заголовок: ;X;Y;Z
                writer.WriteLine(";X;Y;Z");

                for (int i = 0; i < selectedStations.Count; i++)
                {
                    Vector3 pos = selectedStations[i].position;
                    string name = $"Station{i + 1}";
                    writer.WriteLine($"{name};{pos.x:F3};{pos.y:F3};{pos.z:F3}");
                }
            }

            Debug.Log($"✅ Координаты станций экспортированы в: {txtPath}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"❌ Ошибка при записи TXT: {ex.Message}");
        }
    }

    public void ClearStations()
    {
        // удаляет ссылку на выбранные станции; фактические префабы остаются в parentContainer
        selectedStations.Clear();
        Debug.Log("selectedStations очищен.");
    }
}
