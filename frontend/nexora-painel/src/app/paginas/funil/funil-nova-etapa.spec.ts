import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, provideRouter } from '@angular/router';
import { ColunaFunil } from '../../nucleo/modelos';
import { AuthServico } from '../../nucleo/servicos/auth.servico';
import { RealtimeServico } from '../../nucleo/servicos/realtime.servico';
import { RealtimeFalso, rotaFalsa } from '../telas-do-painel';
import { Funil } from './funil';

/** CRIAR ETAPA PELO QUADRO — issue #7.
 *
 *  ===================== O QUE ESTE ARQUIVO CUIDA =====================
 *  A coluna tracejada é pequena de código e carrega três regras que não são óbvias:
 *
 *  1. **Só o dono a vê**, e por `auth.ehDono()` — não por guard de rota. `/crm` é de TODO papel;
 *     o guard está em `/crm/:pipeline/etapas`, que é outra tela.
 *  2. **A etapa nasce na pipeline ATUAL.** Sem o parâmetro ela iria para a padrão, e o dono só
 *     descobriria ao trocar de funil e achar uma coluna que não pediu.
 *  3. **Ela é o último filho do `.quadro`, e isso quase quebrou a faixa de etapas do celular** —
 *     ver o teste do `colunaVisivel`.
 *  ==================================================================== */
describe('funil — criar etapa pelo quadro', () => {
  const COLUNAS: ColunaFunil[] = [
    { etapaId: 1, nome: 'Novo Lead', ordem: 1, cor: '#7FA88B', eGanho: false,
      total: 0, valorTotal: 0, concluidas: 0, contatos: [], temMais: false },
    { etapaId: 2, nome: 'Venda', ordem: 2, cor: '#1E4028', eGanho: true,
      total: 0, valorTotal: 0, concluidas: 0, contatos: [], temMais: false }
  ];

  let fixture: ComponentFixture<Funil>;
  let componente: Funil;
  let http: HttpTestingController;

  function montar(papel: 'dono' | 'vendedor' = 'dono', colunas = COLUNAS) {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: RealtimeServico, useClass: RealtimeFalso },
        // A pipeline 3 — para o teste da criação provar que ela viaja na requisição.
        { provide: ActivatedRoute, useValue: rotaFalsa({ pipeline: '3' }) }
      ]
    });

    TestBed.inject(AuthServico).aplicarLogin({
      token: 'tok',
      usuario: { id: 1, nome: 'Ana Souza', email: 'ana@x.com', papel, empresaNome: 'Padaria' }
    } as never);

    fixture = TestBed.createComponent(Funil);
    componente = fixture.componentInstance;
    http = TestBed.inject(HttpTestingController);

    fixture.detectChanges();
    http.match(r => r.url.includes('/funil')).forEach(r => r.flush({ colunas }));
    http.match(() => true).forEach(r => r.flush({}));
    fixture.detectChanges();
    return fixture;
  }

  function raiz(): HTMLElement { return fixture.nativeElement as HTMLElement; }

  afterEach(() => TestBed.resetTestingModule());

  // ==================================================================== papel
  it('O VENDEDOR NÃO VÊ A COLUNA DE CRIAR', () => {
    // ⚠️ `/crm` é de todo papel — sem este recorte o vendedor veria um botão que lhe daria 403.
    // O enforcement continua no servidor: `EtapasController` inteiro é `Roles = "dono"`.
    montar('vendedor');
    expect(raiz().querySelector('.coluna-nova')).toBeNull();
  });

  it('O DONO VÊ, E ELA É O ÚLTIMO ELEMENTO DO QUADRO', () => {
    // A POSIÇÃO é a mensagem: `CriarAsync` põe sempre no fim, e a coluna tracejada estar no fim
    // responde "onde a etapa vai cair" sem precisar de aviso nenhum.
    montar('dono');

    const quadro = raiz().querySelector('.quadro')!;
    expect(quadro.lastElementChild?.classList.contains('coluna-nova')).toBeTrue();
    expect(raiz().querySelector('.coluna-nova .porque')?.textContent)
      .toContain('no fim deste funil');
  });

  // ==================================================================== a faixa do celular
  /** ⚠️ ESTE É O TESTE QUE PEGOU O DEFEITO QUE A COLUNA CRIOU.
   *
   *  `aoRolarQuadro` percorria `quadro.children` para decidir qual aba da faixa de etapas fica
   *  ativa no celular. Com a coluna tracejada no fim, rolar até o final escolheria ELA, e
   *  `colunaVisivel` viraria um índice sem aba correspondente — a faixa ficaria sem nenhuma
   *  ativa, sem erro nenhum no console.
   *
   *  As duas consultas ao DOM passaram a olhar `.coluna`, e não `children`. */
  it('A FAIXA DE ETAPAS IGNORA A COLUNA DE CRIAR', () => {
    montar('dono');

    const quadro = raiz().querySelector('.quadro') as HTMLElement;
    expect(quadro.children.length)
      .withContext('duas colunas + a tracejada').toBe(COLUNAS.length + 1);

    // Rola até o fim: sem o `.coluna`, `melhor` seria o índice da tracejada.
    Object.defineProperty(quadro, 'scrollLeft', { value: 99_999, writable: true });
    componente.aoRolarQuadro();

    expect(componente.colunaVisivel())
      .withContext('o índice tem de caber na faixa de abas').toBeLessThan(COLUNAS.length);
  });

  // ==================================================================== criar
  it('A ETAPA NASCE NA PIPELINE ABERTA, NÃO NA PADRÃO', () => {
    // Sem o parâmetro, o servidor cai na padrão — e o dono só descobriria ao trocar de funil e
    // achar uma coluna que não pediu.
    montar('dono');
    componente.abrirNovaEtapa();
    componente.fNomeEtapa.set('Visita agendada');
    componente.criarEtapa();

    const pedido = http.expectOne(r => r.method === 'POST' && r.url.includes('/etapas'));
    expect(pedido.request.body.nome).toBe('Visita agendada');
    expect(pedido.request.urlWithParams)
      .withContext('a pipeline da rota viaja na requisição').toContain('pipeline=3');
    pedido.flush({ id: 9 });
  });

  it('NOME CURTO NÃO CHEGA A SAIR DA TELA', () => {
    montar('dono');
    componente.abrirNovaEtapa();
    componente.fNomeEtapa.set('a');
    componente.criarEtapa();

    http.expectNone(r => r.method === 'POST');
    expect(componente.erroNovaEtapa()).toContain('Dê um nome');
  });

  it('O ERRO DO SERVIDOR APARECE E O FORMULÁRIO FICA ABERTO', () => {
    // A mensagem dele distingue "já existe" de "chegou no limite", e fechar obrigaria a redigitar.
    montar('dono');
    componente.abrirNovaEtapa();
    componente.fNomeEtapa.set('Proposta');
    componente.criarEtapa();

    http.expectOne(r => r.method === 'POST').flush(
      { erro: 'Já existe uma etapa chamada "Proposta".' },
      { status: 409, statusText: 'Conflict' });
    fixture.detectChanges();

    expect(componente.criandoEtapa()).withContext('continua aberto').toBeTrue();
    expect(raiz().querySelector('.campo-erro')?.textContent).toContain('Já existe');
  });

  it('O DUPLO CLIQUE NÃO CRIA DUAS ETAPAS', () => {
    montar('dono');
    componente.abrirNovaEtapa();
    componente.fNomeEtapa.set('Visita agendada');
    componente.criarEtapa();
    componente.criarEtapa();

    http.expectOne(r => r.method === 'POST').flush({ id: 9 });
  });

  // ==================================================================== teto
  it('NO TETO DE 12, A COLUNA DIZ O MOTIVO EM VEZ DE CONVIDAR', () => {
    // Desabilitar sem dizer por quê é pior que não desabilitar.
    const cheio = Array.from({ length: 12 }, (_, i) => ({
      etapaId: i + 1, nome: `Etapa ${i}`, ordem: i + 1, cor: '#7FA88B',
      eGanho: i === 11, total: 0, valorTotal: 0, concluidas: 0, contatos: [], temMais: false
    }));
    montar('dono', cheio);

    const botao = raiz().querySelector('.coluna-nova > button') as HTMLButtonElement;
    expect(botao.disabled).toBeTrue();
    expect(botao.title).toContain('Junte ou apague');
    expect(raiz().querySelector('.coluna-nova .porque')?.textContent).toContain('12 etapas');
  });

  // ==================================================================== o que NÃO está aqui
  it('A COLUNA NÃO OFERECE MARCAR GANHO NEM REORDENAR', () => {
    // As duas mudam o significado do histórico de conversão ou dependem de um índice único não
    // adiável — não cabem num gesto de quadro. A dica aponta para onde elas vivem.
    montar('dono');
    componente.abrirNovaEtapa();
    fixture.detectChanges();

    const texto = raiz().querySelector('.coluna-nova')?.textContent ?? '';
    expect(texto).toContain('Entra no fim');
    expect(texto).toContain('Etapas deste funil');
    expect(raiz().querySelector('.coluna-nova input[type=checkbox]')).toBeNull();
  });
});
