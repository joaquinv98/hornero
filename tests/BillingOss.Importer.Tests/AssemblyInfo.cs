// Las pruebas de integración crean una base cada una en un PostgreSQL compartido: con más de cuatro a la vez
// se agotan las conexiones y las escrituras sincrónicas le pegan al almacenamiento del servidor.
[assembly: Parallelize(Workers = 4, Scope = ExecutionScope.MethodLevel)]
