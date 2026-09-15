import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { Subject } from 'rxjs';
import { RealtimeServico } from '../../nucleo/servicos/realtime.servico';
import { AuthServico } from '../../nucleo/servicos/auth.servico';
import { RESPONDEM_ARRAY as LISTAS_DE_ARRAY } from '../telas-do-painel';
import { Contato } from './contato';

/** Criar lembrete COM HORA pela tela.
 *
 *  ===================== O BUG QUE ISTO GUARDA =====================
 *  `<input type="time">` produz `"14:30"`, sem segundos — é o que a especificação do HTML define.
 *  A API exigia `"14:30:00"` e devolvia 400, então o lembrete com hora NUNCA era criado. O
 *  vendedor não abria chamado: concluía que o sistema não presta.
 *
 *  A correção foi na API (conversor que aceita as duas formas). Este teste fixa o outro lado do
 *  contrato: a tela manda o formato CURTO, que é o que o navegador de fato produz. Se alguém
 *  "consertar" aqui mandando `"14:30:00"`, o teste avisa que o conserto foi no lugar errado.
 *  ================================================================= */
describe('Contato — lembrete com hora', () => {
  class RealtimeFalso {
    conectado = signal(true);
    mensagemRecebida$ = new Subject<never>();
    conversaAberta$ = new Subject<never>();
    contatoCriado$ = new Subject<never>();
    statusMensagem$ = new Subject<never>();
    conexaoMudou$ = new Subject<never>();
    async conectar() { }
    desconectar() { }
  }

  let httpMock: HttpTestingController;

  /** Mesmo superset do `paginas.render.spec.ts`: campo a mais o JavaScript ignora, e o que
   *  importa é nenhuma lista chegar `undefined` — o que estouraria por culpa do teste. */
  const CORPO = {
    itens: [], temMais: false, total: 0, colunas: [], etapas: [], lembretes: [],
    contato: {
      id: 7, nome: 'Cliente Teste', telefone: '5584900000000', email: null,
      origem: 'manual', responsavelId: null, valor: null, etapaId: 1, etapaNome: 'Novo Lead',
      ganhoEm: null, perdidoEm: null, criadoEm: '2026-08-01T10:00:00Z',
      conversaId: null, aguardandoDesde: null, naoLidas: 0, ordemKanban: 1000
    },
    // ⚠️ NÃO é 1 de propósito: 1 é o id que a tela pedia HARDCODED, e um fixture com 1 deixaria
    // o teste abaixo passar com o defeito no lugar.
    pipelineId: 9,
    // A lista de negócios vivos — um por funil onde a pessoa está.
    negocios: [{
      id: 55, pipelineId: 9, pipelineNome: 'Vendas', etapaId: 1, etapaNome: 'Novo Lead',
      valor: null, status: 'aberta', ganhaEm: null, versao: 1, etiquetas: []
    }],
    origemDetalhe: null, observacoes: null, motivoPerda: null, anonimizadoEm: null,
    ultimaMensagemEm: null
  };

  // ⚠️ `/etiquetas` PRECISA entrar aqui. O despachante devolve `CORPO` (um objeto) para tudo que
  // não está na lista, e um objeto onde a tela espera array faz o `@for` dos chips estourar — num
  // erro que não se parece nem um pouco com a causa.
  // ⚠️ IMPORTADA de `telas-do-painel`, e não copiada: eram QUATRO listas iguais no projeto e
  // duas já tinham divergido. A que mora lá é a única.
  const RESPONDEM_ARRAY = LISTAS_DE_ARRAY;

  function responderTudo() {
    for (let volta = 0; volta < 5; volta++) {
      const pendentes = httpMock.match(() => true);
      if (pendentes.length === 0) return;
      pendentes.forEach(r =>
        r.flush(RESPONDEM_ARRAY.some(u => r.request.url.includes(u)) ? [] : CORPO));
    }
  }


  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: RealtimeServico, useClass: RealtimeFalso },
        {
          provide: ActivatedRoute,
          useValue: {
            snapshot: {
              paramMap: convertToParamMap({ id: '7' }),
              queryParamMap: convertToParamMap({}),
              data: {}
            }
          }
        }
      ]
    });

    httpMock = TestBed.inject(HttpTestingController);
    TestBed.inject(AuthServico).aplicarLogin({
      token: 't',
      usuario: { id: 1, nome: 'Ana', email: 'a@a.com', papel: 'dono', empresaNome: 'X' }
    } as never);
  });

  afterEach(() => localStorage.clear());

  /** ⚠️ O SELETOR DE ETAPA VINHA VAZIO, E O COMPILADOR NÃO TINHA COMO AVISAR.
   *
   *  A tela chamava `funil.quadro(1)`, escrito quando o primeiro parâmetro era `porColuna` e `1`
   *  queria dizer "um card por coluna" — o certo para uma tela que só quer os NOMES das colunas.
   *
   *  Quando `pipeline` entrou na FRENTE da assinatura, a chamada continuou compilando e passou a
   *  pedir a pipeline de id 1. Funcionava por acidente na primeira empresa, cuja pipeline É a de
   *  id 1; em qualquer outra o combo vinha vazio, sem erro e sem log — e o contato não tinha como
   *  mudar de etapa.
   *
   *  O teste fixa o que importa: a tela pede as etapas DO FUNIL DE CADA NEGÓCIO.
   *
   *  ⚠️ A ROTA MUDOU E FICOU MAIS LEVE. Era `/funil?pipeline=9&porColuna=1` — o QUADRO inteiro
   *  com um card por coluna, só para ler os nomes das colunas. Com a pessoa em vários funis
   *  isso seriam N consultas de quadro; `/etapas?pipeline=` responde a mesma pergunta sem
   *  montar card nenhum. */
  it('O SELETOR DE ETAPA PEDE O FUNIL DO NEGÓCIO, NÃO UM FIXO', () => {
    const fixture = TestBed.createComponent(Contato);
    fixture.detectChanges();

    // O detalhe vem primeiro: é dele que sai a pipeline de cada negócio.
    httpMock.expectOne(r => r.url.endsWith('/contatos/7') && r.method === 'GET').flush(CORPO);
    fixture.detectChanges();

    const etapas = httpMock.expectOne(r => r.url.includes('/etapas') && r.method === 'GET');
    expect(etapas.request.urlWithParams)
      .withContext('a pipeline do negócio, não uma fixa').toContain('pipeline=9');

    etapas.flush([]);
    responderTudo();
  });

  /** ===================== A LISTA MOVE O NEGÓCIO DA LINHA =====================
   *  Relatado como pergunta: "se no detalhe do contato tivesse uma lista de fases/etiquetas onde
   *  o respectivo contato está?".
   *
   *  ⚠️ O SELETOR ANTIGO MANDAVA O ID DO CONTATO. `funil.mover` sempre esperou o da NEGOCIAÇÃO —
   *  os dois são `number`, e trocar um pelo outro COMPILA. Dava "Negócio não encontrado" a cada
   *  mudança de etapa desde o E4c/2, e num banco onde as faixas de id se cruzassem teria movido
   *  o card de outra pessoa.
   *
   *  A fixture usa contato 7 e negócios 55 e 56 de propósito: nenhum dos ids coincide, então o
   *  teste distingue os três caminhos possíveis.
   *  ============================================================================ */
  it('mover pela lista usa o id DAQUELE negócio, e leva a versão', async () => {
    const fixture = TestBed.createComponent(Contato);
    fixture.detectChanges();

    httpMock.expectOne(r => r.url.endsWith('/contatos/7') && r.method === 'GET').flush({
      ...CORPO,
      negocios: [
        { ...CORPO.negocios[0], id: 55, pipelineId: 9, pipelineNome: 'Vendas', versao: 41 },
        {
          id: 56, pipelineId: 12, pipelineNome: 'Pós-venda', etapaId: 30,
          etapaNome: 'Recebido', valor: null, status: 'aberta', ganhaEm: null,
          versao: 77, etiquetas: []
        }
      ]
    });
    fixture.detectChanges();

    // UMA consulta de etapas por FUNIL — duas linhas, dois funis.
    for (const r of httpMock.match(req => req.url.includes('/etapas'))) r.flush([]);
    responderTudo();
    fixture.detectChanges();

    const c = fixture.componentInstance;
    c.moverNegocio(c.negocios()[1], 31);

    const req = httpMock.expectOne(r => r.url.includes('/mover'));
    expect(req.request.url).withContext('o id da NEGOCIAÇÃO da linha').toContain('/56/mover');
    expect(req.request.url).not.toContain('/7/');
    expect(req.request.body.versao).withContext('o xmin do card, para a trava de concorrência').toBe(77);

    req.flush({ ordemKanban: 1 });
    for (const r of httpMock.match(() => true)) r.flush({});
  });

  it('MANDA A HORA NO FORMATO DO NAVEGADOR ("14:30"), e a API aceita', () => {
    const fixture = TestBed.createComponent(Contato);
    fixture.detectChanges();

    responderTudo();
    fixture.detectChanges();

    const c = fixture.componentInstance;
    c.abrirLembrete();
    c.lTitulo.set('Ligar de volta');
    c.lData.set('2026-08-06');
    c.lHora.set('14:30');          // exatamente o que o <input type="time"> entrega
    c.salvarLembrete();

    const req = httpMock.expectOne(r => r.url.includes('/lembretes') && r.method === 'POST');
    const corpo = req.request.body as { horaAlvo: string; dataAlvo: string };

    expect(corpo.horaAlvo).withContext('a tela não deve reformatar a hora').toBe('14:30');
    expect(corpo.dataAlvo).toBe('2026-08-06');

    // A API responde 200 — antes desta correção, respondia 400 aqui.
    req.flush({ id: 1 });
    expect(c.modalLembrete()).withContext('o modal fecha quando salva').toBeFalse();
  });

  /** ===================== O SELECT DE ETAPA MENTIA =====================
   *
   *  Relatado assim: "o contato Ysia está em Negociação, mas na tela de contato está em Novo
   *  Lead". O banco estava certo e a trilha também; quem mentia era o `<select>`.
   *
   *  A causa é de DOM, não de Angular: `[value]` num `<select>` é aplicado quando o contato
   *  chega, e as `<option>` vêm de OUTRA requisição (`/funil/quadro`). Um select sem a opção
   *  correspondente descarta o valor em silêncio e passa a exibir a primeira — "Novo Lead".
   *  Quando as opções chegam depois, a ligação NÃO roda de novo, porque `c.etapaId` não mudou.
   *
   *  ⚠️ E não era só cosmético: este select é o controle que MOVE o contato de etapa. A tela
   *  dizia "Novo Lead" para todo contato que não estivesse na primeira etapa, e quem confiasse
   *  nela mexeria no funil às cegas.
   *  ===================================================================== */
  it('MOSTRA A ETAPA REAL, mesmo com as opções chegando depois do contato', async () => {
    const fixture = TestBed.createComponent(Contato);
    fixture.detectChanges();

    // A ORDEM É O TESTE. Primeiro o contato — na etapa 3, que não é a primeira — e só depois o
    // quadro com as opções. É a ordem que acontece de fato: são duas requisições paralelas.
    const detalhe = httpMock.expectOne(r => r.url.endsWith('/contatos/7') && r.method === 'GET');
    detalhe.flush({
      ...CORPO,
      contato: { ...CORPO.contato, etapaId: 3, etapaNome: 'Negociação' },
      negocios: [{ ...CORPO.negocios[0], etapaId: 3, etapaNome: 'Negociação' }]
    });
    fixture.detectChanges();

    const etapas = httpMock.expectOne(r => r.url.includes('/etapas'));
    etapas.flush([
      { id: 1, nome: 'Novo Lead', ordem: 1, cor: '#7FA88B', eGanho: false, contatos: 0 },
      { id: 2, nome: 'Proposta', ordem: 2, cor: '#7FA88B', eGanho: false, contatos: 0 },
      { id: 3, nome: 'Negociação', ordem: 3, cor: '#7FA88B', eGanho: false, contatos: 0 }
    ]);
    fixture.detectChanges();

    responderTudo();
    fixture.detectChanges();

    // ⚠️ `whenStable`, e não só `detectChanges`. O `NgModel` aplica o valor na view por
    // MICROTAREFA (`resolvedPromise.then`) — num app zoneless o `detectChanges` síncrono
    // termina antes disso, e a asserção leria o select ainda vazio. No navegador a
    // microtarefa roda no mesmo instante; aqui ela precisa ser esperada.
    await fixture.whenStable();

    // ⚠️ SEM `#etapa`: o seletor único virou UM POR LINHA da lista de negócios, e um id
    // repetido em N linhas seria HTML inválido. A busca é pelo select DENTRO da linha.
    const select = (fixture.nativeElement as HTMLElement)
      .querySelector('.negocio-controles select') as HTMLSelectElement;

    expect(select).withContext('o select existe').not.toBeNull();
    // O texto da opção MARCADA, e não `select.value`: com `[ngValue]` o valor do DOM é um id
    // interno do Angular (`"2: 3"`). Quem vê a tela lê o texto.
    expect(select.selectedIndex).withContext('nenhuma opção marcada').toBeGreaterThanOrEqual(0);
    expect(select.options[select.selectedIndex].textContent!.trim())
      .withContext('o select está mostrando a etapa errada').toBe('Negociação');
  });

  it('sem hora, manda null — lembrete só com data continua valendo', () => {
    const fixture = TestBed.createComponent(Contato);
    fixture.detectChanges();
    responderTudo();
    fixture.detectChanges();

    const c = fixture.componentInstance;
    c.abrirLembrete();
    c.lTitulo.set('Retomar contato');
    c.lData.set('2026-08-06');
    c.salvarLembrete();

    const req = httpMock.expectOne(r => r.url.includes('/lembretes') && r.method === 'POST');
    expect((req.request.body as { horaAlvo: string | null }).horaAlvo).toBeNull();
    req.flush({ id: 2 });
  });
});
