# Nexora

## Regra de negócio fica no backend

Toda regra de negócio fica no backend (.NET). O frontend (Angular) só recebe os dados prontos e
renderiza.

- Estado, permissão, validação e elegibilidade são calculados pela API.
- Regra de exibição também: o que aparece, junto com o quê, sem repetir e com qual estilo. A API
  manda a lista pronta e as flags, e o template só faz `@for`/`@if` sobre o que chegou.
- Validação no painel só como conveniência de digitação, nunca como a regra.
- Exceção registrada: o semáforo de SLA (`nucleo/semaforo.ts`), que precisa envelhecer na tela sem
  novo fetch.
