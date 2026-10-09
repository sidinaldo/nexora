import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { AuthServico } from '../../nucleo/servicos/auth.servico';
import { ToastServico } from '../../nucleo/toast/toast.servico';
import { PERMISSOES_DE } from '../../nucleo/seguranca/permissoes-de-teste';
import { ConfiguracaoEmpresa } from '../../nucleo/modelos';
import { AbaConfiguracoes, Configuracoes } from './configuracoes';

/** ===================== O LIGA/DESLIGA DA CONCLUSÃO DA VENDA (POS-1) =====================
 *
 *  Esta tela não tinha teste nenhum, e este arquivo não tenta cobri-la: cobre o controle que o
 *  POS-1 acrescentou, que é o único desta tela capaz de desfazer uma decisão do dono em silêncio.
 *
 *  As duas coisas que podem quebrar calado:
 *
 *    1. o campo de dias desaparecer ou PERDER O VALOR quando a chave é desligada. O número é
 *       guardado de propósito — é o que faz religar devolver o prazo que a empresa tinha, em vez
 *       de 7 por acidente;
 *
 *    2. o PUT sair sem o campo. O `CorpoAtendimento` o tem como obrigatório, então omitir não
 *       compila — mas mandar um literal em vez do sinal compila, e o dono salvaria a tela toda
 *       vendo o valor certo na frente dele e outro no banco.
 *  ====================================================================================== */
describe('configurações — a conclusão da venda liga e desliga', () => {
  let fixture: ComponentFixture<Configuracoes>;
  let c: Configuracoes;
  let http: HttpTestingController;

  /** Os feriados são uma página do servidor (AUD-XX, #21). */
  const SEM_FERIADOS = { itens: [], totalCount: 0, pagina: 1, tamanhoPagina: 20, totalPaginas: 1 };

  const CONFIG: ConfiguracaoEmpresa = {
    nome: 'Softio', documento: null, fusoHorario: 'America/Sao_Paulo', uf: 'RN',
    janelaHoraInicio: 8, janelaHoraFim: 20, janelaDiasSemana: 126,
    semaforoAmareloMinutos: 60, semaforoVermelhoMinutos: 240,
    diasSemRespostaFollowUp: 2,
    // 30 e não 7: um valor que a empresa escolheu, para o teste poder provar que ele sobrevive.
    diasParaConcluirVenda: 30,
    conclusaoAutomatica: true,
    // A pesquisa LIGADA na fixture, com valores que a empresa escolheu — para o teste poder provar
    // que eles chegam na tela e voltam no PUT. O padrão de verdade é desligada.
    npsAtivo: true,
    npsDiasAposConclusao: 5,
    npsDiasExpiracao: 2,
    npsTexto: '{{saudacao}} Aqui é da {{empresa}}. De 0 a 10?',
    npsMensagemPromotor: 'Valeu!',
    npsMensagemDetrator: null,
    resumoDiarioAtivo: false
  };

  /** `aba`: a tela abre em Empresa (UI-XX). Os testes do horário e da pesquisa vão à aba deles. */
  function montar(sobrepor: Partial<ConfiguracaoEmpresa> = {}, aba: AbaConfiguracoes = 'empresa') {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(), provideRouter([]),
        provideHttpClient(), provideHttpClientTesting()
      ]
    });

    TestBed.inject(AuthServico).aplicarLogin({
      token: 'tok',
      usuario: {
        id: 1, nome: 'Dona', email: 'd@x.com', papel: 'dono',
        permissoes: PERMISSOES_DE.dono, empresaNome: 'Softio'
      }
    } as never);

    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(Configuracoes);
    c = fixture.componentInstance;
    fixture.detectChanges();

    // A tela dispara quatro leituras no `ngOnInit`; só a primeira interessa aqui, e as outras
    // falham de propósito sem derrubar nada (é o comportamento declarado do componente).
    for (const r of http.match(() => true)) {
      if (r.request.url.endsWith('/configuracao')) r.flush({ ...CONFIG, ...sobrepor });
      else if (r.request.url.endsWith('/feriados')) r.flush(SEM_FERIADOS);
      else r.flush([]);
    }
    fixture.detectChanges();

    if (aba !== 'empresa') {
      c.trocarAba(aba);
      fixture.detectChanges();
    }
    return fixture;
  }

  afterEach(() => http.verify());

  const campoDias = () =>
    fixture.nativeElement.querySelector('#cv') as HTMLInputElement;

  it('carrega LIGADO e com o campo de dias editável', () => {
    montar({}, 'atendimento');

    expect(c.fConclusaoAuto()).toBeTrue();
    expect(c.fDiasConcluir()).toBe(30);
    expect(campoDias().disabled).toBeFalse();
  });

  it('carrega DESLIGADO quando é isso que o servidor diz', async () => {
    // ⚠️ ESTE TESTE EXISTE PORQUE O DE CIMA NÃO PROVA NADA SOZINHO. O sinal nasce `true`, e a
    // configuração padrão também é `true` — então um `carregar()` que simplesmente IGNORASSE a
    // resposta do servidor passaria. Descobri sabotando: apaguei a linha que lê o campo e nenhum
    // teste caiu.
    //
    // Uma empresa que desligou a conclusão abriria a tela vendo o interruptor ligado, e o primeiro
    // "salvar" de qualquer outro campo religaria a feature em silêncio.
    montar({ conclusaoAutomatica: false }, 'atendimento');
    await fixture.whenStable();

    expect(c.fConclusaoAuto()).toBeFalse();
    expect(campoDias().disabled).toBeTrue();
    expect(fixture.nativeElement.textContent).toContain('Nada é concluído sozinho');
  });

  it('DESLIGAR desabilita o campo de dias e MANTÉM o número', async () => {
    montar({}, 'atendimento');

    c.fConclusaoAuto.set(false);
    fixture.detectChanges();
    // ⚠️ `[disabled]` é escrito no DOM por microtask, não no `detectChanges`. Sem este `await` o
    // teste lê o DOM velho e passa por acidente — já aconteceu neste projeto.
    await fixture.whenStable();

    expect(campoDias().disabled).withContext('o campo fica travado').toBeTrue();
    expect(c.fDiasConcluir()).withContext('mas o valor NÃO se perde').toBe(30);
    expect(campoDias()).withContext('e o campo continua na tela').not.toBeNull();
  });

  it('o aviso troca: desligado, diz que o card fica no quadro e segura a vaga do funil', async () => {
    montar({}, 'atendimento');

    expect(fixture.nativeElement.textContent).toContain('o relógio para');

    c.fConclusaoAuto.set(false);
    fixture.detectChanges();
    await fixture.whenStable();

    const texto = fixture.nativeElement.textContent as string;
    expect(texto).toContain('Nada é concluído sozinho');
    // ⚠️ ESTA FRASE É O PREÇO HONESTO DE DESLIGAR, e esta é a única tela onde alguém o escolhe.
    // Sem ela, o sintoma é o funil deixar de aparecer na hora de abrir um negócio novo, sem
    // nenhuma explicação em lugar nenhum.
    expect(texto).toContain('não pode abrir outro negócio neste funil');
  });

  it('o PUT de atendimento manda a conclusão automática, com o valor da tela', () => {
    montar();

    c.fConclusaoAuto.set(false);
    c.salvarAtendimento();

    const req = http.expectOne(r => r.url.endsWith('/configuracao/atendimento'));

    expect(req.request.body.conclusaoAutomatica)
      .withContext('o valor da tela, não um literal').toBeFalse();
    expect(req.request.body.diasParaConcluirVenda)
      .withContext('e o prazo vai junto, mesmo desligado').toBe(30);

    req.flush(null);
    // O componente recarrega depois de salvar.
    for (const r of http.match(() => true)) {
      if (r.request.url.endsWith('/configuracao')) r.flush(CONFIG);
      else if (r.request.url.endsWith('/feriados')) r.flush(SEM_FERIADOS);
      else r.flush([]);
    }
  });

  // ==================================================================== pesquisa pós-venda (NPS-1)

  /** Drena as leituras que o `carregar()` refaz depois de salvar — o `http.verify()` do `afterEach`
   *  cobra cada uma. Mesmo laço que o teste do atendimento já usava no fim. */
  function drenarRecarga() {
    for (const r of http.match(() => true)) {
      if (r.request.url.endsWith('/configuracao')) r.flush(CONFIG);
      else if (r.request.url.endsWith('/feriados')) r.flush(SEM_FERIADOS);
      else r.flush([]);
    }
    fixture.detectChanges();
  }

  it('A CONFIGURAÇÃO DA PESQUISA CHEGA NA TELA E VOLTA NO PUT', () => {
    montar();

    expect(c.fNpsAtivo()).toBeTrue();
    expect(c.fNpsDias()).toBe(5);
    expect(c.fNpsExpiracao()).toBe(2);
    expect(c.fNpsPromotor()).toBe('Valeu!');
    // Nulo chega como string vazia: o `<textarea>` não aceita nulo.
    expect(c.fNpsDetrator()).toBe('');

    c.salvarPesquisaNps();

    const req = http.expectOne(r => r.url.endsWith('/configuracao/pesquisa-nps'));
    expect(req.request.method).toBe('PUT');
    expect(req.request.body).toEqual({
      npsAtivo: true,
      npsDiasAposConclusao: 5,
      npsDiasExpiracao: 2,
      npsTexto: '{{saudacao}} Aqui é da {{empresa}}. De 0 a 10?',
      npsMensagemPromotor: 'Valeu!',
      // ⚠️ VAZIO VIRA NULO: os dois querem dizer "não envia", e string em branco deixaria o campo
      // dizendo "há uma mensagem" sem mensagem nenhuma.
      npsMensagemDetrator: null
    });

    req.flush(null);
    drenarRecarga();
  });

  /** ===================== O `npsAtivo` VAI SEMPRE, MESMO FALSO =====================
   *
   *  ⚠️ A API o recebe ANULÁVEL e RECUSA nulo, e a razão é esta: omitido no corpo, ele chegaria
   *  como `false` — um valor VÁLIDO, que desligaria a pesquisa por uma escolha que ninguém fez.
   *  ============================================================================= */
  it('DESLIGAR A PESQUISA MANDA FALSE, E NÃO OMITE O CAMPO', () => {
    montar();

    c.fNpsAtivo.set(false);
    c.salvarPesquisaNps();

    const req = http.expectOne(r => r.url.endsWith('/configuracao/pesquisa-nps'));

    expect(Object.keys(req.request.body as object)).toContain('npsAtivo');
    expect((req.request.body as { npsAtivo: boolean }).npsAtivo).toBeFalse();

    req.flush(null);
    drenarRecarga();
  });

  /** ===================== A PRÉ-VISUALIZAÇÃO MOSTRA O CASO RUIM =====================
   *
   *  ⚠️ É O PONTO TODO DELA. Quem escreve `"Oi, {{nome}}!"` vê a prévia boa e salva; só a prévia
   *  SEM nome revela "Oi, !" — que é o que recebe o cliente cujo WhatsApp não manda o nome do
   *  perfil. `NomeDePessoa` documenta esse defeito tendo acontecido de verdade, com "(84)" no
   *  lugar do nome.
   *  ============================================================================== */
  it('A PRÉ-VISUALIZAÇÃO REVELA O "Oi, !" DE QUEM NÃO TEM NOME', () => {
    montar();

    c.fNpsTexto.set('Oi, {{nome}}! Aqui é da {{empresa}}.');

    expect(c.previaComNome()).toBe('Oi, Maria! Aqui é da Softio.');
    // O caso que o dono não imagina.
    expect(c.previaSemNome()).toBe('Oi, ! Aqui é da Softio.');
    expect(c.previasDiferentes()).withContext('a tela mostra os dois').toBeTrue();
  });

  it('COM {{saudacao}} OS DOIS CASOS FICAM CERTOS', () => {
    montar();

    c.fNpsTexto.set('{{saudacao}} Aqui é da {{empresa}}.');

    expect(c.previaComNome()).toBe('Oi, Maria! Aqui é da Softio.');
    // Sem nome, a pontuação acompanha — é o que `NomeDePessoa.Saudacao` resolve no servidor.
    expect(c.previaSemNome()).toBe('Oi! Aqui é da Softio.');
  });

  /** ⚠️ VARIÁVEL INVENTADA SAI LITERAL no WhatsApp do cliente, com as chaves e tudo. O aviso é a
   *  única chance de o dono perceber antes de salvar. */
  it('VARIÁVEL QUE NÃO EXISTE É DENUNCIADA', () => {
    montar();

    c.fNpsTexto.set('Oi {{telefone}}, de 0 a 10?');
    expect(c.temVariavelDesconhecida()).toBeTrue();

    c.fNpsTexto.set('{{saudacao}} De 0 a 10?');
    expect(c.temVariavelDesconhecida()).toBeFalse();
  });

  /** A frase do sucesso diz QUANDO vale. Sem isso o dono muda o texto e acha que corrigiu o que já
   *  saiu — e pesquisa agendada mantém o texto que recebeu. */
  it('SALVAR DESLIGA O ESTADO DE SALVANDO E RECARREGA A TELA', () => {
    montar();

    c.salvarPesquisaNps();
    expect(c.salvandoNps()).toBeTrue();

    http.expectOne(r => r.url.endsWith('/configuracao/pesquisa-nps')).flush(null);
    drenarRecarga();

    expect(c.salvandoNps()).toBeFalse();
  });

  /** ===================== A PRÉVIA TEM DE ESTAR NA TELA, NÃO SÓ NO `computed` =====================
   *
   *  ⚠️ ESTE TESTE NASCEU DE UMA SABOTAGEM QUE NÃO DERRUBAVA NADA. Troquei o `@if (previasDiferentes())`
   *  do template por `@if (false)` — a prévia do caso ruim desaparecia da tela — e os cinco testes
   *  anteriores continuavam verdes, porque todos leem o `computed` e nenhum lê o DOM.
   *
   *  E a prévia do caso ruim é o ponto todo da seção: ela é a única chance de o dono ver "Oi, !"
   *  antes de o cliente ver.
   *  ============================================================================================== */
  it('OS DOIS BALÕES DE PRÉVIA APARECEM NA TELA', () => {
    montar({}, 'pesquisa');

    c.fNpsTexto.set('Oi, {{nome}}! Aqui é da {{empresa}}.');
    fixture.detectChanges();

    const baloes = [...(fixture.nativeElement as HTMLElement)
      .querySelectorAll('.previa-nps .previa-balao p')].map(e => e.textContent!.trim());

    expect(baloes.length).withContext('o bom e o ruim, lado a lado').toBe(2);
    expect(baloes[0]).toBe('Oi, Maria! Aqui é da Softio.');
    expect(baloes[1]).toBe('Oi, ! Aqui é da Softio.');
  });

  /** Com `{{saudacao}}` as duas prévias dão certo, e aí o segundo balão SAI da tela: repetir a
   *  mesma frase duas vezes não ensina nada e vira ruído. */
  it('COM AS DUAS PRÉVIAS IGUAIS, O SEGUNDO BALÃO NÃO APARECE', () => {
    montar({}, 'pesquisa');

    c.fNpsTexto.set('De 0 a 10, quanto você recomendaria?');
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement)
      .querySelectorAll('.previa-nps .previa-balao').length).toBe(1);
  });

  /** O aviso de variável desconhecida também mora no template. */
  it('O AVISO DE VARIÁVEL DESCONHECIDA APARECE NA TELA', () => {
    montar({}, 'pesquisa');

    const aviso = () => (fixture.nativeElement as HTMLElement).querySelector('.aviso-variavel');

    c.fNpsTexto.set('{{saudacao}} De 0 a 10?');
    fixture.detectChanges();
    expect(aviso()).toBeNull();

    c.fNpsTexto.set('Oi {{telefone}}, de 0 a 10?');
    fixture.detectChanges();
    expect(aviso()).not.toBeNull();
  });

  /** "Página X de Y" é o do SERVIDOR (AUD-XX, #21). Os números são de propósito impossíveis para
   *  o tamanho da página — se a tela voltar a dividir o total, o teste mostra outra conta. */
  it('AS PÁGINAS DOS FERIADOS SÃO AS DO SERVIDOR', () => {
    montar();

    c.irParaFeriado(3);
    const pedido = http.expectOne(r => r.url.endsWith('/feriados'));
    expect(pedido.request.params.get('pagina')).toBe('3');
    // 45 feriados de 20 em 20 seriam 3 páginas; o servidor diz 7.
    pedido.flush({
      itens: [{ id: 1, data: '2026-12-25', nome: 'Natal', abrangencia: 'nacional', ehManual: false, ignorado: false }],
      totalCount: 45, pagina: 3, tamanhoPagina: 20, totalPaginas: 7
    });
    fixture.detectChanges();

    expect(c.totalPaginasFeriado()).toBe(7);
    expect(c.totalFeriados()).toBe(45);
  });

  // ==================================================================== resumo diário (RES-XX)
  it('LIGAR O RESUMO DIÁRIO SALVA NO CLIQUE', () => {
    montar();
    expect(c.fResumoDiario()).toBeFalse();

    c.alternarResumoDiario();
    const req = http.expectOne(r => r.url.endsWith('/configuracao/resumo-diario'));
    expect(req.request.method).toBe('PUT');
    expect(req.request.body).toEqual({ ativo: true });
    req.flush(null);

    expect(c.fResumoDiario()).toBeTrue();
    expect(c.salvandoResumo()).toBeFalse();
  });

  /** O interruptor não pode mostrar ligado o que o servidor não gravou. */
  it('SE O SERVIDOR RECUSAR, O INTERRUPTOR VOLTA', () => {
    montar({ resumoDiarioAtivo: true });
    expect(c.fResumoDiario()).toBeTrue();

    c.alternarResumoDiario();
    http.expectOne(r => r.url.endsWith('/configuracao/resumo-diario'))
      .flush({ erro: 'Sem permissão.' }, { status: 403, statusText: 'Forbidden' });

    expect(c.fResumoDiario()).toBeTrue();
  });

  // ==================================================================== reenviar o resumo (RES-XX)
  const botaoReenviar = () => [...(fixture.nativeElement as HTMLElement).querySelectorAll('button')]
    .find(b => b.textContent!.includes('Reenviar o resumo')) as HTMLButtonElement | undefined;

  it('O REENVIAR SÓ APARECE COM O RESUMO LIGADO', () => {
    montar({ resumoDiarioAtivo: false });
    expect(botaoReenviar()).toBeUndefined();
  });

  it('REENVIAR PEDE AO SERVIDOR E DIZ O DIA QUE SAIU', () => {
    montar({ resumoDiarioAtivo: true });
    const sucesso = spyOn(TestBed.inject(ToastServico), 'sucesso');

    botaoReenviar()!.click();
    http.expectOne(r => r.url.endsWith('/configuracao/resumo-diario/reenviar') && r.method === 'POST')
      .flush({ dia: '2026-10-08', enviados: 1, donos: 2 });

    expect(sucesso).toHaveBeenCalledWith('Resumo de 08/10 enviado para 1 de 2 donos.');
    expect(c.reenviandoResumo()).toBeFalse();
  });

  /** Nenhum e-mail saiu: o servidor responde erro, e a tela mostra a frase dele — e não "enviado". */
  it('SE O E-MAIL NÃO SAIU, A TELA DIZ O QUE O SERVIDOR DISSE', () => {
    montar({ resumoDiarioAtivo: true });
    const erro = spyOn(TestBed.inject(ToastServico), 'erro');

    c.reenviarResumo();
    http.expectOne(r => r.url.endsWith('/configuracao/resumo-diario/reenviar'))
      .flush({ erro: 'O e-mail não saiu: o servidor de e-mail recusou.' }, { status: 502, statusText: 'Bad Gateway' });

    expect(erro).toHaveBeenCalledWith('O e-mail não saiu: o servidor de e-mail recusou.');
  });

  // ==================================================================== as abas (UI-XX)
  /** Cada aba mostra só as suas seções — a tela era uma coluna só, e quem vinha mudar o horário
   *  rolava pela pesquisa inteira. */
  it('SÃO TRÊS ABAS, E CADA UMA MOSTRA SÓ AS SUAS SEÇÕES', () => {
    montar();
    const secoes = () => [...(fixture.nativeElement as HTMLElement).querySelectorAll('h2')]
      .map(h => h.textContent!.trim());

    expect([...(fixture.nativeElement as HTMLElement).querySelectorAll('[role="tab"]')]
      .map(b => b.textContent!.trim())).toEqual(['Empresa', 'Atendimento', 'Pesquisa pós-venda']);
    expect(secoes()).toEqual(['Dados da empresa', 'Resumo diário']);

    c.trocarAba('atendimento');
    fixture.detectChanges();
    expect(secoes()).toEqual(['Horário de atendimento', 'Feriados']);

    c.trocarAba('pesquisa');
    fixture.detectChanges();
    expect(secoes()).toEqual(['Pesquisa pós-venda']);
  });

  /** O que foi digitado numa aba não se perde ao olhar outra: os campos moram no componente. */
  it('TROCAR DE ABA NÃO PERDE O QUE FOI DIGITADO', async () => {
    montar();
    c.fNome.set('Softio Matriz');

    c.trocarAba('pesquisa');
    fixture.detectChanges();
    c.trocarAba('empresa');
    fixture.detectChanges();
    // O `ngModel` escreve no campo num microtask: sem esperar, o teste leria o campo vazio.
    await fixture.whenStable();

    expect((fixture.nativeElement.querySelector('#nome') as HTMLInputElement).value).toBe('Softio Matriz');
  });
});
