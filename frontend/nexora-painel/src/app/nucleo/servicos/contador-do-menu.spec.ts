import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ContatosServico } from './contatos.servico';
import { FunilServico } from './funil.servico';
import { PipelinesServico } from './pipelines.servico';
import { VendasServico } from './vendas.servico';

/** ===================== O CONTADOR DO MENU FICAVA VELHO =====================
 *
 *  Relatado assim: "quando incluí um card em negociação o contador do menu só funcionou depois
 *  do refresh".
 *
 *  A lista de funis é carregada UMA vez, pelo shell, no boot — e o número ao lado de cada um é
 *  "negócios no quadro". Toda ação que muda esse número o deixava velho até alguém recarregar a
 *  página inteira.
 *
 *  ⚠️ ESTE TESTE É UMA TABELA, E ISSO É O PONTO. São OITO ações espalhadas por três serviços, e
 *  o modo de falhar não é uma delas estar errada — é alguém acrescentar a nona e não saber que
 *  precisava recontar. A tabela torna a omissão visível: a ação nova entra aqui ou não entra.
 *
 *  Este projeto já pagou duas vezes pelo mesmo descuido — `quadro(1)` escrito em duas telas e
 *  `RESPONDEM_ARRAY` em quatro cópias, duas delas divergentes.
 *
 *  ⚠️ E É POR ISSO QUE O RECARREGAMENTO MORA NO SERVIÇO, não na tela. Uma tela nova que chame
 *  `contatosApi.abrirNegociacao(...)` acerta o contador sem saber que ele existe.
 *  ========================================================================== */
describe('o contador do menu recarrega sozinho', () => {
  let http: HttpTestingController;

  function montar() {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting()
      ]
    });
    http = TestBed.inject(HttpTestingController);
    TestBed.inject(PipelinesServico);   // o dono da lista que o menu lê
  }

  afterEach(() => http.verify({ ignoreCancelled: true }));

  /** Cada linha: o nome da ação e o que dispará-la.
   *
   *  ⚠️ OS SERVIÇOS CHEGAM COMO ARGUMENTO, e não injetados aqui: cada iteração monta um TestBed
   *  novo, e um serviço resolvido do TestBed anterior leva junto o `HttpTestingController`
   *  destruído — o sintoma é um `NG0908` que não tem nada a ver com o que se está testando. */
  type Acao = { nome: string; disparar: (c: ContatosServico, v: VendasServico, f: FunilServico) => void };

  const ACOES: Acao[] = [
      // abre negócio: +1 no funil escolhido — a ação do relato
      { nome: 'abrir negociação', disparar: c => c.abrirNegociacao(7, null).subscribe() },
      // nasce contato COM negócio (criação manual abre um)
      { nome: 'criar contato', disparar: c => c.criar({ nome: 'X' } as never).subscribe() },
      // com `dias = 0` a venda já nasce concluída e sai do quadro
      { nome: 'marcar ganho', disparar: c => c.marcarGanho(7, 100).subscribe() },
      // perdido sai do quadro: -1
      { nome: 'marcar perdido', disparar: c => c.marcarPerdido(7, 'caro').subscribe() },
      // anonimizado sai do quadro (`RegrasNegociacao.NoQuadro`): -1
      { nome: 'anonimizar', disparar: c => c.anonimizar(7).subscribe() },
      // concluir tira o card da coluna de ganho: -1
      { nome: 'concluir venda', disparar: (_c, v) => v.concluir([3]).subscribe() },
      // cancelar tira a ganha e devolve uma aberta
      { nome: 'cancelar venda', disparar: (_c, v) => v.cancelar(3).subscribe() },
      // arrastar ENTRE funis mexe em DOIS contadores
      { nome: 'mover card', disparar: (_c, _v, f) => f.mover(3, 9, null).subscribe() }
  ];

  it('todas as ações que mudam o quadro recarregam a lista de funis', () => {
    for (const { nome, disparar } of ACOES) {
      TestBed.resetTestingModule();
      montar();
      disparar(
        TestBed.inject(ContatosServico), TestBed.inject(VendasServico), TestBed.inject(FunilServico));

      // A própria ação primeiro...
      const acao = http.match(r => !r.url.endsWith('/pipelines'));
      expect(acao.length).withContext(`${nome}: a requisição da ação`).toBe(1);
      acao[0].flush({});

      // ...e o menu logo depois.
      const menu = http.match(r => r.url.endsWith('/pipelines') && r.method === 'GET');
      expect(menu.length).withContext(`${nome} NÃO recarregou o contador do menu`).toBe(1);
      menu[0].flush([]);

      http.verify();
    }
  });

  /** ⚠️ FALHAR EM RECONTAR NÃO PODE DERRUBAR A AÇÃO. O usuário acabou de abrir a negociação com
   *  sucesso; um erro ao buscar a lista do menu é um número velho na tela, não uma operação
   *  perdida — e mostrar erro ali faria parecer que a negociação não foi criada. */
  it('erro ao recarregar o menu não vaza para quem chamou', () => {
    TestBed.resetTestingModule();
    montar();
    const contatos = TestBed.inject(ContatosServico);

    let deuCerto = false;
    let deuErro = false;
    contatos.abrirNegociacao(7, null).subscribe({
      next: () => { deuCerto = true; },
      error: () => { deuErro = true; }
    });

    http.expectOne(r => r.url.endsWith('/negociacao')).flush({});
    http.expectOne(r => r.url.endsWith('/pipelines'))
      .flush({ erro: 'falhou' }, { status: 500, statusText: 'Erro' });

    expect(deuCerto).withContext('a ação continua tendo sucesso').toBeTrue();
    expect(deuErro).withContext('o erro do menu não vaza').toBeFalse();
  });
});
