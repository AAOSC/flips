﻿namespace Flips

open System.Collections.Generic
open Flips.Types

module internal Dictionary =

    let tryFind k (d:Dictionary<_,_>) =
        match d.TryGetValue k with
        | true, v -> Some v
        | false, _ -> None


module internal ORTools =
    open Google.OrTools.LinearSolver

    type internal OrToolsSolverType =
        | CBC
        | GLOP

    let private createVariable (solver:Solver) (DecisionName name:DecisionName) (decisionType:DecisionType) =
        match decisionType with
        | Boolean -> solver.MakeBoolVar(name)
        | Integer (lb, ub) -> solver.MakeIntVar(float lb, float ub, name)
        | Continuous (lb, ub) -> solver.MakeNumVar(float lb, float ub, name)


    let addVariable (solver:Solver) decisionName (decisionType:DecisionType) (vars:Dictionary<DecisionName, Variable>) =
        if not (vars.ContainsKey(decisionName)) then
            let var = createVariable solver decisionName decisionType
            vars.[decisionName] <- var

    let private buildExpression solver (vars:Dictionary<DecisionName,Variable>) (expr:LinearExpression) =
        let reducedExpr = Flips.Types.LinearExpression.Reduce expr

        let decisionExpr =
            reducedExpr.Coefficients
            |> Seq.map (fun kvp ->
                          addVariable solver kvp.Key reducedExpr.DecisionTypes.[kvp.Key] vars
                          kvp.Value * vars.[kvp.Key])
            |> Array.ofSeq

        let offsetExpr = LinearExpr() + reducedExpr.Offset
        let resultExpr = decisionExpr.Sum() + offsetExpr
        resultExpr


    let private setObjective (vars:Dictionary<DecisionName, Variable>) (objective:Flips.Types.Objective) (solver:Solver) =
        let expr = buildExpression solver vars objective.Expression

        match objective.Sense with
        | Minimize -> solver.Minimize(expr)
        | Maximize -> solver.Maximize(expr)


    let private addEqualityConstraint (vars:Dictionary<DecisionName, Variable>) (ConstraintName n:ConstraintName) (lhs:LinearExpression) (rhs:LinearExpression) (solver:Solver) =
        let lhsExpr = buildExpression solver vars lhs
        let rhsExpr = buildExpression solver vars rhs
        // note: this is work around for
        // https://github.com/google/or-tools/issues/2231
        // https://github.com/matthewcrews/flips/issues/104
        let dictionary = new Dictionary<_, _>()
        let mutable num = lhsExpr.Visit(dictionary)
        num <- num + rhsExpr.DoVisit(dictionary, -1.0)
        let c = solver.MakeConstraint(0.0 - num, 0.0 - num, n)
        for item in dictionary do
            c.SetCoefficient(item.Key, item.Value)


    let private addInequalityConstraint (vars:Dictionary<DecisionName, Variable>) (ConstraintName n:ConstraintName) (lhs:LinearExpression) (rhs:LinearExpression) (inequality:Inequality) (solver:Solver) =
        let lhsExpr = buildExpression solver vars lhs
        let rhsExpr = buildExpression solver vars rhs
        let constraintExpr = lhsExpr - rhsExpr
        
        let lb, ub =
            match inequality with
            | LessOrEqual -> System.Double.NegativeInfinity, 0.0
            | GreaterOrEqual -> 0.0, System.Double.PositiveInfinity

        // note: this is work around for
        // https://github.com/google/or-tools/issues/2231
        // https://github.com/matthewcrews/flips/issues/104
        let dictionary = new Dictionary<_, _>()
        let num = constraintExpr.Visit(dictionary)
        let c = solver.MakeConstraint(lb - num, ub - num, n)
        for item in dictionary do
            c.SetCoefficient(item.Key, item.Value)


    let private addConstraint (vars:Dictionary<DecisionName, Variable>) (c:Types.Constraint) (solver:Solver) =
        match c.Expression with
        | Equality (lhs, rhs) -> addEqualityConstraint vars c.Name lhs rhs solver
        | Inequality (lhs, inequality, rhs) -> addInequalityConstraint vars c.Name lhs rhs inequality solver


    let private addConstraints (varMap:Dictionary<DecisionName, Variable>) (constraints:FSharp.Collections.List<Types.Constraint>) (solver:Solver) =
        for c in constraints do
            addConstraint varMap c solver |> ignore


    let private buildSolution (decisions:seq<Decision>) (vars:Dictionary<DecisionName, Variable>) (solver:Solver) (objective:Types.Objective) =
        let decisionMap =
            decisions
            |> Seq.map (fun d -> d, Dictionary.tryFind d.Name vars
                                    |> Option.map (fun var -> var.SolutionValue ())
                                    |> Option.defaultValue 0.0)
            |> Map.ofSeq

        {
            DecisionResults = decisionMap
            ObjectiveResult = Flips.Types.LinearExpression.Evaluate (fun d -> decisionMap.[d]) objective.Expression
        }


    let private writeLPFile (solver:Solver) (filePath:string) =
        let lpFile = solver.ExportModelAsLpFormat(false)
        System.IO.File.WriteAllText(filePath, lpFile)


    let private writeMPSFile (solver:Solver) (filePath:string) =
        let lpFile = solver.ExportModelAsMpsFormat(false, false)
        System.IO.File.WriteAllText(filePath, lpFile)


    let internal solveForObjective (vars:Dictionary<DecisionName, Variable>) (objective:Flips.Types.Objective) (solver:Solver) =
        setObjective vars objective solver

        let resultStatus = solver.Solve()

        match resultStatus with
        | Solver.ResultStatus.OPTIMAL ->
            Result.Ok (solver,objective)
        | _ ->
            Result.Error resultStatus



    let internal addObjectiveAsConstraint (vars:Dictionary<DecisionName, Variable>) (objective:Flips.Types.Objective) (objectiveValue:float) (solver:Solver) =
        let objectiveAsConstraint =
            let (ObjectiveName name) = objective.Name
            match objective.Sense with
            | Maximize ->
                Constraint.create name (objective.Expression >== objectiveValue)
            | Minimize ->
                Constraint.create name (objective.Expression <== objectiveValue)

        // The underlying API is mutable :/
        addConstraint vars objectiveAsConstraint solver |> ignore
        solver

    let rec internal solveForObjectives (vars:Dictionary<DecisionName, Variable>) (objectives:Flips.Types.Objective list) (solver:Solver) =

        match objectives with
        | [] ->
            failwith "Model without Objective" // Argument should be a special type
        | objective :: [] ->
            solveForObjective vars objective solver
        | objective :: remaining ->
            solveForObjective vars objective solver
            |> Result.map (fun (solver,_) -> addObjectiveAsConstraint vars objective (solver.Objective().BestBound()) solver)
            |> Result.bind (solveForObjectives vars remaining)


    let internal solve (solverType:OrToolsSolverType) (settings:SolverSettings) (model:Flips.Model.Model) =

        let solver =
            match solverType with
            | CBC -> Solver.CreateSolver("CBC")
            | GLOP -> Solver.CreateSolver("GLOP")

        solver.SetTimeLimit(settings.MaxDuration)
        // TODO: add OptimalityGap Here

        // We will enable this in the next major release
        //if settings.EnableOutput then
        //    solver.EnableOutput()
        //else
        //    solver.SuppressOutput()

        let vars = Dictionary()
        addConstraints vars model.Constraints solver
        
        model.Objectives
        |> List.tryHead 
        |> Option.iter (fun objective -> 
          // Write LP/MPS Formulation to file if requested with first objective
          setObjective vars objective solver
          settings.WriteLPFile |> Option.iter (writeLPFile solver)
          settings.WriteMPSFile |> Option.iter (writeMPSFile solver)
        )

        let result = solveForObjectives vars (List.rev model.Objectives) solver

        match result with
        | Result.Ok (solver,objective) ->
            match model.Objectives with
            | firstObjective :: _ when firstObjective <> objective ->
              // Write LP/MPS Formulation to file again if requested
              // doing it again in case the solved objective isn't the first one
              settings.WriteLPFile |> Option.iter (writeLPFile solver)
              settings.WriteMPSFile |> Option.iter (writeMPSFile solver)
            | [] | [_] | _ -> () 

            buildSolution (Model.getDecisions model) vars solver model.Objectives.[0]
            |> SolveResult.Optimal
        | Result.Error errorStatus ->
            match errorStatus with
            | Solver.ResultStatus.INFEASIBLE ->
                SolveResult.Infeasible "The model was found to be infeasible"
            | Solver.ResultStatus.UNBOUNDED ->
                SolveResult.Unbounded "The model was found to be unbounded"
            | _ ->
                SolveResult.Unknown "The model status is unknown. Unable to solve."


module internal Optano =
    open OPTANO.Modeling.Optimization

    type internal OptanoSolverType =
        | Cplex128
        | Gurobi912


    let private createVariable (DecisionName name:DecisionName) (decisionType:DecisionType) =
        match decisionType with
        | Boolean -> new Variable(name, 0.0, 1.0, Enums.VariableType.Binary)
        | Integer (lb, ub) -> new Variable(name, lb, ub, Enums.VariableType.Integer)
        | Continuous (lb, ub) -> new Variable(name, lb, ub, Enums.VariableType.Continuous)


    let addVariable decisionName (decisionType:DecisionType) (vars:Dictionary<DecisionName, Variable>) =
        if not (vars.ContainsKey(decisionName)) then
            let var = createVariable decisionName decisionType
            vars.[decisionName] <- var


    let private buildExpression (vars:Dictionary<DecisionName,Variable>) (expr:LinearExpression) =
        let reducedExpr = Flips.Types.LinearExpression.Reduce expr

        let constantExpr = Expression.Sum([reducedExpr.Offset])
        let decisionExpr =
            reducedExpr.Coefficients
            |> Seq.map (fun kvp ->
                          addVariable kvp.Key reducedExpr.DecisionTypes.[kvp.Key] vars
                          kvp.Value * vars.[kvp.Key])
            |> (fun terms -> Expression.Sum(terms))

        constantExpr + decisionExpr


    let private addObjective (vars:Dictionary<DecisionName, Variable>) (objective:Flips.Types.Objective) priority (optanoModel:Model) =
        let (ObjectiveName name) = objective.Name
        let expr = buildExpression vars objective.Expression

        let sense =
            match objective.Sense with
            | Minimize -> Enums.ObjectiveSense.Minimize
            | Maximize -> Enums.ObjectiveSense.Maximize

        let optanoObjective = new Objective(expr, name, sense, priority)
        optanoModel.AddObjective(optanoObjective)

    let private addObjectives (vars:Dictionary<DecisionName, Variable>) (objectives:Flips.Types.Objective list) (optanoModel:Model) =
        let numOfObjectives = objectives.Length

        objectives
        // The order of the objectives is the priority order
        // OPTANO sorts objectives by PriorityLevel desc
        |> List.iteri (fun i objective -> addObjective vars objective (numOfObjectives - i) optanoModel)

        optanoModel


    let private addEqualityConstraint (vars:Dictionary<DecisionName, Variable>) (ConstraintName n:ConstraintName) (lhs:LinearExpression) (rhs:LinearExpression) (optanoModel:Model) =
        let lhsExpr = buildExpression vars lhs
        let rhsExpr = buildExpression vars rhs
        let c = Constraint.Equals(lhsExpr, rhsExpr)
        optanoModel.AddConstraint(c, n)


    let private addInequalityConstraint (vars:Dictionary<DecisionName, Variable>) (ConstraintName n:ConstraintName) (lhs:LinearExpression) (rhs:LinearExpression) (inequality:Inequality) (optanoModel:Model) =
        let lhsExpr = buildExpression vars lhs
        let rhsExpr = buildExpression vars rhs

        let c =
            match inequality with
            | LessOrEqual ->
                Constraint.LessThanOrEqual(lhsExpr, rhsExpr)
            | GreaterOrEqual ->
                Constraint.GreaterThanOrEqual(lhsExpr, rhsExpr)

        optanoModel.AddConstraint(c, n)


    let private addConstraint (vars:Dictionary<DecisionName, Variable>) (c:Types.Constraint) (optanoModel:Model) =
        match c.Expression with
        | Equality (lhs, rhs) -> addEqualityConstraint vars c.Name lhs rhs optanoModel
        | Inequality (lhs, inequality, rhs) -> addInequalityConstraint vars c.Name lhs rhs inequality optanoModel


    let private addConstraints (vars:Dictionary<DecisionName, Variable>) (constraints:FSharp.Collections.List<Types.Constraint>) (optanoModel:Model) =
        for c in constraints do
            addConstraint vars c optanoModel |> ignore



    let private buildSolution (decisions:seq<Decision>) (vars:Dictionary<DecisionName, Variable>) (optanoSolution:Solution) (objective:Flips.Types.Objective) =
        let decisionMap =
            decisions
            |> Seq.map (fun d ->
                            match Dictionary.tryFind d.Name vars with
                            | Some var -> d, var.Value
                            | None -> d, 0.0)
            |> Map.ofSeq

        {
            DecisionResults = decisionMap
            ObjectiveResult = Flips.Types.LinearExpression.Evaluate (fun d -> decisionMap.[d]) objective.Expression
        }


    let private writeLPFile (optanoModel:Model) (filePath:string) =
        if System.IO.File.Exists(filePath) then
            System.IO.File.Delete(filePath)
        use outputStream = new System.IO.FileStream(filePath, System.IO.FileMode.CreateNew)
        let lpExporter = Exporter.LPExporter(outputStream)
        lpExporter.Write(optanoModel)

    let private writeMPSFile (optanoModel:Model) (filePath:string) =
        if System.IO.File.Exists(filePath) then
            System.IO.File.Delete(filePath)
        use outputStream = new System.IO.FileStream(filePath, System.IO.FileMode.CreateNew)
        let exporter = Exporter.MPSExporter(outputStream)
        exporter.Write(optanoModel)


    let private gurobi912Solve (settings:Types.SolverSettings) (optanoModel:Model) =
        use solver = new Solver.Gurobi912.GurobiSolver()
        solver.Configuration.TimeLimit <- float settings.MaxDuration / 1000.0
        solver.Configuration.MIPGap <- settings.OptimalityGap
        solver.Solve(optanoModel)


    let private cplex128Solve (settings:Types.SolverSettings) (optanoModel:Model) =
        use solver = new Solver.Cplex128.CplexSolver()
        solver.Configuration.TimeLimit <- float settings.MaxDuration / 1000.0
        solver.Configuration.MIPGap <- settings.OptimalityGap
        solver.Solve(optanoModel)


    let internal solve (solverType:OptanoSolverType) (settings:SolverSettings) (model:Flips.Model.Model) =

        let optanoModel = new Model()
        let vars = Dictionary()
        addConstraints vars model.Constraints optanoModel
        let orderedObjective = List.rev model.Objectives
        addObjectives vars orderedObjective optanoModel |> ignore

        settings.WriteLPFile |> Option.iter (writeLPFile optanoModel)
        settings.WriteMPSFile |> Option.iter (writeMPSFile optanoModel)

        let optanoSolution =
            match solverType with
            | Cplex128 -> cplex128Solve settings optanoModel
            | Gurobi912 -> gurobi912Solve settings optanoModel

        match optanoSolution.ModelStatus, optanoSolution.Status with
        | Solver.ModelStatus.Feasible, (Solver.SolutionStatus.Optimal | Solver.SolutionStatus.Feasible) ->
            let solution = buildSolution (Model.getDecisions model) vars optanoSolution (List.head orderedObjective)
            Optimal solution
        | Solver.ModelStatus.Infeasible, _ ->
            SolveResult.Infeasible "The model was found to be infeasible"
        | Solver.ModelStatus.Unbounded, _ ->
            SolveResult.Unbounded "The model was found to be unbounded"
        | _ ->
            SolveResult.Unknown "The model status is unknown. Unable to solve."


[<RequireQualifiedAccess>]
module Solver =

    /// <summary>The function used to call the underlying solver with the model</summary>
    /// <param name="settings">The settings for the solver to use</param>
    /// <param name="model">A model which represents the problem</param>
    /// <returns>A solution which contains results if successful or a message in case of an error</returns>
    let solve (settings:SolverSettings) (model:Flips.Model.Model) =

        match settings.SolverType with
        | CBC -> ORTools.solve ORTools.OrToolsSolverType.CBC settings model
        | GLOP -> ORTools.solve ORTools.OrToolsSolverType.GLOP settings model
        | Cplex128 -> Optano.solve Optano.OptanoSolverType.Cplex128 settings model
        | Gurobi912 -> Optano.solve Optano.OptanoSolverType.Gurobi912 settings model


