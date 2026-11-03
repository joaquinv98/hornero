// Como en las del núcleo: cada prueba con base crea la suya en el PostgreSQL local, y más de cuatro a la vez lo saturan.
[assembly: Parallelize(Workers = 4, Scope = ExecutionScope.MethodLevel)]
